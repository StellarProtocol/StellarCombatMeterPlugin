using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

// Combat-event capture region: the OnCombatEvent dispatch and its per-channel accumulators
// (dealt / heal / taken), plus the spec + resonance observe helpers. Split out of Plugin.cs to
// keep each file under the 500-LoC cap (Phase 3 adds more here). Behaviour is identical.
public sealed partial class Plugin
{
    private void OnCombatEvent(CombatEvent evt)
    {
        // SP1: capture every event into the log buffer (runs even when the meter display is paused).
        MaybeCaptureForLog(evt);

        if (_paused) return;
        if (evt is CombatEvent.SkillUsed su) { LogSkillUsed(su); return; }
        if (evt is CombatEvent.EntitySummonAppeared sa) { ObserveSummonAppeared(sa); return; }
        if (evt is not CombatEvent.DamageDealt d) return;

        // Run-boundary combat-belt (Task 4, Plugin.RunBoundary.cs): MUST run before
        // MaybeCutForBossPhase/EnsureCombatStarted below so a commit here resets the run-scoped
        // trackers (stage bosses etc.) before this same event's boss-phase cut or combat-start latch
        // could attribute it to the WRONG (stale) run. Hot path when not armed costs one field read.
        ResolveArmedBoundaryBelt();

        // Inline boss-phase cut (Task 7, 2026-07-21). Capture whether combat was ALREADY running before
        // this event establishes it, then — on the FIRST combat event involving the boss — cut the
        // pre-boss trash into its own segment IMMEDIATELY (no settle defer) so this first boss hit lands
        // in the FRESH boss segment. MUST run BEFORE _agg/EnsureCombatStarted/CaptureTaken/Accumulate
        // below, or the first hit leaks into the archived trash (the owner's chopped-fight bug). O(1) +
        // alloc-free once warm: admission into the _stageBosses set short-circuits via the _bossCheck
        // cache (Plugin.BossDetection.cs), and the CUT itself short-circuits via EventInvolvesAnyStageBoss
        // once a segment is already active (multi-boss plan Task 2, 2026-08-12 — the set replaced the
        // single _autoArchiveBossId latch this comment used to name).
        bool priorCombat = _combatActive;
        MaybeCutForBossPhase(d.SourceId, d.TargetId, d.TimestampMs, priorCombat);

        // Establish combat start from the FIRST event of ANY channel (dealt / heal / taken). Previously
        // the latch lived in AccumulateDamage, so an encounter that opened with a heal or an incoming hit
        // dropped those events from the timeline (the `if (_combatActive)` guards were unsatisfied) and
        // skewed bucket indices against a later, damage-established start. Hoisting it here fixes that for
        // healers/tanks. When damage is the first event, _combatStartMs is identical to the old behaviour.
        EnsureCombatStarted(d.TimestampMs);

        _agg.AddDamage(d.SourceId, d.Amount, d.IsHeal);

        // Damage taken: accrue onto the TARGET's stats (so Taken-mode can rank/aggregate victims).
        if (!d.IsHeal && d.TargetId.IsPlayer) CaptureTaken(d);

        // Elite capture (ELITE CAPTURE channel, owner ruling 2026-08-13, Plugin.EliteDetection.cs):
        // TOGGLE-INDEPENDENT — no _autoArchive.BossEnabled gate, unlike MaybeCutForBossPhase's boss
        // admission above (which only runs when Boss phase is on). Feeds ONLY the capture-only
        // _eliteSet — never AutoArchive/BossStatus/verdict/bossId paths.
        ObserveEliteCandidates(d.SourceId, d.TargetId);

        // Replay: note both source and target BEFORE the player-only early-out so boss/add target ids
        // (e.g. a mob being hit by a player) also enter the entity set for position tracking.
        // (Boss detection for the auto-archive trigger moved to MaybeCutForBossPhase above, which runs
        // before accumulation — see the inline-cut comment at the top of OnCombatEvent.)
        NoteReplayEntity(d.SourceId, d.TargetId);

        // Per-source stats/timeline: PLAYERS ONLY — mirror the _agg guard above. Mob sources are never
        // shown (live rows come from _agg, which discards non-players; History/SkillBreakdown are
        // player-focused), so a SourceStats (2 dicts) + SourceTimeline (3 dicts) per mob was pure dead
        // weight. In a multi-round dungeon that grew _stats/_timelines with every mob ever seen (Clear()
        // only fires on scene change/archive, never between rounds), ballooning the managed heap and
        // driving the GC-pressure FPS decay users hit at the same dungeon spot each round.
        if (!d.SourceId.IsPlayer) return;

        var s = StatsFor(d.SourceId);

        ObserveResonanceCast(d);
        if (d.IsHeal) { CaptureHeal(s, d); return; }
        AccumulateDamage(s, d);
    }

    // Combat-start latch. Set once by the first combat event of any channel; reset by Clear().
    private void EnsureCombatStarted(long timestampMs)
    {
        if (_combatActive) return;
        _combatActive  = true;
        _combatStartMs = timestampMs;
        // A new encounter's combat re-arms the once-per-run clear-marker guard (ShouldBankEmptyClearMarker),
        // so the NEXT run's late clear can bank its own marker.
        _clearMarkerBanked = false;
        // Reset the run-scoped clear latch HERE too (vault-floor P0, run sea/qyvCSXteqC). This is the ONLY
        // reset point, and it runs on the NEXT encounter's first combat event — which is strictly AFTER the
        // outgoing floor's run-end (scene) archive banks (you leave the cleared floor, its OnSceneChanged
        // archive fires, THEN the next floor's combat starts here). So a cleared floor's latch is still set
        // when its own archive reads it, and cannot bleed into the next floor that never cleared. Not reset
        // by Clear() (per-archive) — see the field doc in Plugin.cs.
        _clearedThisRun = false;
        _clearedSettlement = null;
        // Latch the dungeon run-id mid-run (valid here) as a fallback: IDungeonState.CurrentRunId can reset
        // to 0 on scene-leave, which may be exactly when the archive fires. ManualArchive uses this if the
        // live id is already 0 at archive time.
        _lastRunId     = _services.Dungeon.CurrentRunId;
        // Latch the party id (GrpcTeam team_id) too, for the same reason: the server keys a run's
        // identity on the party (docs/superpowers/specs/2026-08-04-run-identity-party-teamkey-design.md),
        // so a mid-run/post-run party change (member leaves, party disbands, re-forms) must not
        // retroactively relabel this encounter. See LatchTeamId (Plugin.History.cs) for the read-side.
        _lastTeamId    = _services.PartySnapshot.PartyId;
        // Baseline for the false-KILL fix: remember whatever LastSettlement already read BEFORE this
        // encounter started, so archive-time can tell a fresh kill (settlement changed since combat
        // started) apart from a stale settlement carried over from an earlier segment of the same run.
        _settlementAtCombatStart = _services.Dungeon.LastSettlement;
        // Latch the Master difficulty level too — it arrives at scene-enter (before combat) and
        // survives here, but CurrentDifficulty is reset to 0 on a run-id change that can precede
        // archive (fail-out to a result/lobby scene), which dropped the "Master N" level on fails.
        _difficultyAtCombatStart = _services.Dungeon.CurrentDifficulty;
    }

    // ------------------------------------------------------------------------------------------------
    // Per-boss/per-elite statistics — CAPTURE-ONLY bucket routing (Spec B,
    // docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §3). Split boss vs elite exactly
    // like bosses[]/elites[] (owner ruling 2026-08-13: elites never reach boss surfaces). Routing READS
    // _stageBosses/_eliteSet and feeds NOTHING back into AutoArchiveEngine, BossStatus, the verdict,
    // junk suppression, bossId/bosses[], or run identity — the whole-fight _stats stay the single source
    // of truth for every existing surface (archive-flow invariants untouched:
    // docs/recon/combatmeter-archive-flow.md). Bucket width/cap are the whole-fight timeline constants
    // and the anchor is the same (combat start), so a per-bucket series starts out aligned with the
    // whole-fight one. It does NOT stay aligned by construction: each cell owns its own SourceTimeline
    // and coalesces INDEPENDENTLY (its BucketMs doubles when THAT cell outruns the cap), so on a very
    // long fight cadences can diverge — they are normalized to one bucketMs at emission
    // (LogUpload/DerivedBucketBuilder.ResolveBucketMs), which is what makes the site's chart swap
    // like-for-like.
    // ------------------------------------------------------------------------------------------------
    private readonly TargetBucketStats _bossBuckets  = new(TimelineBucketMs, TimelineMaxBuckets);
    private readonly TargetBucketStats _eliteBuckets = new(TimelineBucketMs, TimelineMaxBuckets);

    /// <summary>Pure bucket precedence: a tracked stage boss wins; else a tracked elite (routed to its
    /// OWN store via <c>isElite</c>); else <see cref="TargetBucketStats.OtherKey"/> on the boss store.
    /// Boss beats elite because a real boss can be RE-TYPED to Elite in event/rotation content
    /// (<c>MonsterType</c> is not stable per name — docs/recon/raid-clear-and-multiboss.md), and the
    /// surface it is TRACKED on must win. Pinned headless (Plugin cannot be instantiated in tests —
    /// repo pattern, see <see cref="ObserveBurstHit"/>).</summary>
    internal static (bool isElite, int bucketKey) RouteTargetBucket(
        bool bossMember, int bossConfigId, bool eliteMember, int eliteConfigId)
    {
        if (bossMember) return (false, bossConfigId);
        if (eliteMember) return (true, eliteConfigId);
        return (false, TargetBucketStats.OtherKey);
    }

    // Both sets are probed (not short-circuited on the boss hit) so RouteTargetBucket alone decides
    // precedence — the overlap case is a re-typed boss, not a theoretical one. Alloc-free: two bounded
    // MaxMembers scans, no interop call and no admission logic (membership is whatever the engine and
    // the elite channel already admitted).
    private (bool isElite, int bucketKey) ResolveTargetBucket(EntityId id)
    {
        var boss  = _stageBosses.TryGetConfigId(id, out var bossConfigId);
        var elite = _eliteSet.TryGetConfigId(id, out var eliteConfigId);
        return RouteTargetBucket(boss, bossConfigId, elite, eliteConfigId);
    }

    // Get-or-create the per-source aggregate.
    private SourceStats StatsFor(EntityId id)
    {
        if (!_stats.TryGetValue(id, out var s))
        {
            s = new SourceStats();
            _stats[id] = s;
        }
        return s;
    }

    // Healing accrues to the source's total + per-skill heal counters + healing timeline.
    // Heal hits/crits/luckys are tracked SEPARATELY from the damage counters — the upload's heal
    // breakdown previously borrowed the damage-path Hits/Crits (0/0 for pure heals, wrong for hybrids).
    private void CaptureHeal(SourceStats s, CombatEvent.DamageDealt d)
    {
        s.TotalHealing += d.Amount;
        s.HealHits += 1;
        if (d.IsCrit) { s.HealCrits += 1; s.CritHealing += d.Amount; }
        if (d.IsLucky) { s.HealLuckys += 1; s.LuckyHealing += d.Amount; }
        if (d.IsCrit && d.IsLucky) { s.HealCritLuckys += 1; s.CritLuckyHealing += d.Amount; }
        if (d.Amount > s.TopHeal) s.TopHeal = d.Amount;
        s.EffectiveHealing += d.ActualAmount;   // real applied heal; total − effective = overheal
        if (!s.BySkill.TryGetValue(d.SkillId, out var hsk)) { hsk = new SkillStats(); s.BySkill[d.SkillId] = hsk; }
        hsk.HealTotal += d.Amount;
        hsk.HealHits  += 1;
        if (d.IsCrit) hsk.HealCrits += 1;
        if (d.IsLucky) hsk.HealLuckys += 1;
        if (d.Amount > hsk.HealTop) hsk.HealTop = d.Amount;
        if (_combatActive) TimelineFor(d.SourceId).Add(TimelineChannel.Healing, d.TimestampMs, _combatStartMs, d.Amount);
    }

    // Incoming damage to the target: total taken + per-attacker-skill breakdown + taken timeline.
    private void CaptureTaken(CombatEvent.DamageDealt d)
    {
        // Taken uses d.Amount (the gross Value/HpLessen/Lucky-precedence field the DPS path uses), NOT
        // d.ActualAmount — ActualValue is usually 0 on the wire, so Taken always read 0 in a long fight.
        _agg.AddTaken(d.TargetId, d.Amount);
        var ts = StatsFor(d.TargetId);
        ts.TotalTaken += d.Amount;
        if (d.IsDead) { ts.Deaths += 1; _deaths.Add(new DeathEntry(d.TimestampMs, d.TargetId, d.SkillId)); }
        if (!ts.IncomingBySkill.TryGetValue(d.SkillId, out var inc)) { inc = new IncomingSkillStats(); ts.IncomingBySkill[d.SkillId] = inc; }
        inc.Total += d.Amount; inc.Hits += 1; if (d.Amount > inc.TopHit) inc.TopHit = d.Amount;
        if (!_combatActive) return;
        TimelineFor(d.TargetId).Add(TimelineChannel.Taken, d.TimestampMs, _combatStartMs, d.Amount);
        // Spec B bucket routing (capture-only). For TAKEN the bucket is the ATTACKER (d.SourceId); the
        // victim (d.TargetId) is the store's player key, mirroring the whole-fight line above. The ms is
        // FIGHT-ANCHORED here because the store passes startMs:0 to its own SourceTimeline — the two
        // series must land in identical bucket indices or the site's per-bucket chart swap skews.
        var (isElite, bucketKey) = ResolveTargetBucket(d.SourceId);
        (isElite ? _eliteBuckets : _bossBuckets)
            .AddTaken(d.TargetId, bucketKey, d.Amount, d.TimestampMs - _combatStartMs);
    }

    private void AccumulateDamage(SourceStats s, CombatEvent.DamageDealt d)
    {
        _lastDamageMs = d.TimestampMs;

        s.TotalDamage += d.Amount;
        TimelineFor(d.SourceId).Add(TimelineChannel.Dealt, d.TimestampMs, _combatStartMs, d.Amount);
        s.Hits        += 1;
        if (d.IsCrit) { s.Crits += 1; s.CritDamage += d.Amount; }
        if (d.IsLucky) { s.Luckys += 1; s.LuckyDamage += d.Amount; }
        if (d.IsCrit && d.IsLucky) { s.CritLuckys += 1; s.CritLuckyDamage += d.Amount; }
        if (d.IsDead) s.Kills += 1;
        s.ShieldBreak += d.ShieldAbsorbed;
        if (d.Amount > s.TopHit) s.TopHit = d.Amount;
        if (s.FirstHitMs == 0) s.FirstHitMs = d.TimestampMs;
        s.LastHitMs = d.TimestampMs;

        if (!s.BySkill.TryGetValue(d.SkillId, out var sk))
        {
            sk = new SkillStats();
            s.BySkill[d.SkillId] = sk;
        }
        sk.Total += d.Amount;
        sk.Hits  += 1;
        if (d.IsCrit) sk.Crits += 1;
        if (d.IsLucky) sk.Luckys += 1;
        if (d.IsCrit && d.IsLucky) sk.CritLuckys += 1;
        if (d.IsDead) sk.Kills += 1;
        if (d.Amount > sk.TopHit) sk.TopHit = d.Amount;
        if (d.Amount > 0 && (sk.MinHit == 0 || d.Amount < sk.MinHit)) sk.MinHit = d.Amount;

        // Spec B bucket routing (capture-only). DEALT only — heals returned via CaptureHeal before this
        // method and are never bucketed (healing targets players, not bosses: spec §2). Fight-anchored ms,
        // same reason as CaptureTaken's: the store's own series is built with startMs:0.
        var (isElite, bucketKey) = ResolveTargetBucket(d.TargetId);
        (isElite ? _eliteBuckets : _bossBuckets)
            .AddDealt(d.SourceId, bucketKey, d.SkillId, d.Amount, d.IsCrit, d.TimestampMs - _combatStartMs);
    }

    // Spec comes from the framework's shared cast-resolved cache (ICombatSpec): the framework recognises
    // spec-defining skill ids as events flow, so every consumer (this meter + EntityInspector) agrees. The
    // meter no longer infers spec from the AOI loadout (that carries both specs' signature skills →
    // mislabelled players, e.g. Falconry shown as Wildpack). Returns 0 until a spec-defining skill is cast.
    private int ResolveSpec(EntityId id) => StickySpec(id, _services.CombatSpec.GetSubProfession(id));

    // Timeline cast log: one entry per cast, from the two PROVEN detection signals (the same ones the
    // meter's imagine-cooldown display already runs on — nothing here invents a new id-matching scheme):
    //   SELF   — the LocalCooldowns snapshot: the wire moves an imagine row's skill_begin_time only ON
    //            CAST (ImagineCooldownCalc's documented contract, in-game-validated by the self slot).
    //            A begin advance IS a cast, timestamped at the press instant — works pre-combat and
    //            regardless of whether the summon ever deals damage. See DetectSelfImagineCasts.
    //   OTHERS — DamageDealt hits whose SkillId resolves via IGameDataResonance.GetImagineForSkill
    //            (leveled skill_level_id → SkillFightLevelTable.SkillId → base) — exactly how
    //            ResonanceTracker has always identified foreign casts. First hit of a burst records;
    //            the burst-gap logic below collapses the summon's ongoing action stream into ONE cast.
    //            The recorded TIMESTAMP is nudged earlier using CombatEvent.EntitySummonAppeared (see
    //            ObserveSummonAppeared / ResolveImagineCastMs) when a summon owned by the same player
    //            appeared shortly before the first hit — this trims the summon's wind-up out of the
    //            recorded cast time without needing an id-mapping table (no offline table links an
    //            imagine skill id to a summoned MonsterId or a granted buff id — recon'd and confirmed
    //            absent from StarResonanceData's SkillTable/SkillEffectTable/MonsterTable/BuffTable; see
    //            task-foreign-imagine-report.md). Non-damaging imagines (e.g. a pure haste buff with no
    //            summon and no hit) remain undetected — no wire signal attributes them to a caster.
    // Capped as a runaway guard — 20 players × a cast every ~30s stays far below the cap for any fight.
    private const int MaxImagineCasts = 600;

    // A long-lived summon keeps dealing damage under the same imagine-resolvable skill id for its whole
    // lifetime, so a new cast entry is only recorded when a hit arrives after at least this much SILENCE
    // for the (src, base) key. Every observed hit refreshes the key's last-seen time — the old code
    // compared against the last RECORDED time instead, so a persistent summon minted a phantom "cast"
    // every 5s of sustained damage (the 1:09 no-stacks-left bubble). Residual: recasting the SAME
    // imagine while its previous summon is still landing hits merges into one entry.
    internal const long ImagineRetriggerGapMs = 10_000;

    // Pure burst-gap transition — extracted so it's unit-testable without a live Plugin instance
    // (Plugin is IL2CPP-service-bound and cannot be headless-instantiated; see ReplayCaptureTests'
    // ReplayToggleTests doc comment for the established pattern). Refreshes lastSeen[key] to ms and
    // returns true when this hit starts a NEW burst (no prior sighting, or ≥ gapMs of silence).
    internal static bool ObserveBurstHit(
        Dictionary<(EntityId, int), long> lastSeen, (EntityId, int) key, long ms, long gapMs)
    {
        long? last = lastSeen.TryGetValue(key, out var l) ? l : null;
        lastSeen[key] = ms;
        return last is null || ms - last.Value >= gapMs;
    }

    private void AddImagineCast(EntityId src, int baseSkillId, long ms)
    {
        if (_imagineCasts.Count >= MaxImagineCasts) return;
        _imagineCasts.Add(new ImagineCastEntry(ms, src, baseSkillId));
        LogImagineCastRecorded(src, baseSkillId, ms);
    }

    // Feed the inferred-others cooldown tracker + (for others) the cast log when a Battle-Imagine hit
    // is seen. GetImagineForSkill is null for non-imagine skills. Multi-charge skills recharge on
    // EnergyChargeTime; single-charge on the per-cast cooldown.
    private void ObserveResonanceCast(CombatEvent.DamageDealt d)
    {
        if (!d.SourceId.IsPlayer) return;
        if (_services.ResonanceData.GetImagineForSkill(d.SkillId) is not { } info) return;
        int ms = info.ChargeCount > 1 ? info.RechargeMs : info.CooldownMs;
        // Key by the BASE imagine skill id (info.SkillId), not the leveled cast id (d.SkillId), so OtherSlot —
        // which looks the tracker up by the equipped loadout's base id — finds it.
        _resTracker.OnCast(d.SourceId, info.SkillId, info.ChargeCount, ms, _services.CombatSnapshot.ServerNowMs);
        // Self casts are recorded by the authoritative LocalCooldowns begin-advance detector (press-time,
        // pre-combat-capable); recording them from damage too would double-count the same cast ~seconds late.
        if (d.SourceId.Value == _services.CombatSnapshot.LocalEntityId.Value) return;
        if (!ObserveBurstHit(_lastImagineHitMs, (d.SourceId, info.SkillId), d.TimestampMs, ImagineRetriggerGapMs)) return;
        long? appearMs = _summonAppearMs.TryGetValue(d.SourceId, out var a) ? a : null;
        AddImagineCast(d.SourceId, info.SkillId, ResolveImagineCastMs(d.TimestampMs, appearMs, SummonAppearWindowMs));
    }

    // ------------------------------------------------------------------------------------------------
    // Others — early timestamp anchor from CombatEvent.EntitySummonAppeared (see the field's XML doc:
    // it fires only when an appearing entity's AttrCollection carries AttrTopSummonerId/AttrSummonerId).
    // Keyed by the OWNER (player), not the summon's own entity id — DamageDealt.SourceId is already
    // resolved to the same owner (TopSummonerId ?? AttackerUuid), so the two correlate directly without
    // needing to track the summon's raw uuid or which specific imagine it belongs to.
    // ------------------------------------------------------------------------------------------------

    // How far back an appear may reach to anchor a hit's cast time. Generous over the recon'd ~6s
    // wind-up symptom; a stale appear well outside this window falls back to the hit's own timestamp.
    internal const long SummonAppearWindowMs = 8000;

    private readonly Dictionary<EntityId, long> _summonAppearMs = new();

    private void ObserveSummonAppeared(CombatEvent.EntitySummonAppeared sa)
    {
        if (!sa.SummonerId.IsPlayer) return;
        _summonAppearMs[sa.SummonerId] = sa.TimestampMs;
        LogSummonAppeared(sa);
        TryRecordImagineCastFromAppear(sa);
    }

    // ------------------------------------------------------------------------------------------------
    // Others — appear-SOURCED cast recording (2026-08-14): companions that produce NO damage/heal wire
    // events (buff-only imagines — Tina's haste companion et al.) are invisible to the damage-path
    // detector above (ObserveResonanceCast), so their casts never recorded for OTHER players. The
    // summon's APPEAR is the one wire signal such a cast still emits: resolve the summon entity's
    // monster config id and probe its imagine identity via the framework's SANCTIONED NN=00 synthetic
    // composite, GetImagineForSkill(configId * 100) — SkillAoyiTable's MonsterId column holds both
    // monster-summon ids (10084 Celestial Flier) and companion "- Resonance" ids (3000033 Tina), so
    // the one probe covers both (see the framework's ImagineAoyiRule class doc for the contract).
    // Self stays on the authoritative LocalCooldowns begin-advance detector — never recorded here.
    // ------------------------------------------------------------------------------------------------

    // Summon entities already sighted this run — the novelty gate (see SeenSummonSet's doc: an AOI
    // blink/re-entry of the SAME summon entity is never a new cast). Run-scoped: cleared by
    // ResetRunScopedTrackers (Plugin.RunBoundary.cs), NOT by Clear().
    private readonly SeenSummonSet _seenSummons = new();

    // Why an appear may be recorded as a cast, or why it was skipped. Record is the only recording
    // outcome; the rest are the pure gate's reject reasons (surfaced verbatim in the [img-appear]
    // diagnostics line via AppearGateTag, Plugin.Diagnostics.cs).
    internal enum AppearCastGate { Record, SelfSummoner, RepeatSummon, OwnerNotInCombat }

    /// <summary>Pure record/skip decision for an appear-sourced imagine cast (repo pattern:
    /// <see cref="ObserveBurstHit"/>, <see cref="ResolveImagineCastMs"/> — pinned headless, Plugin
    /// cannot be instantiated in tests). In gate order:
    /// <list type="bullet">
    /// <item><c>summonerIsSelf</c> — self casts are recorded by the authoritative LocalCooldowns
    /// begin-advance detector (press-time, pre-combat-capable); recording them here too would
    /// double-count.</item>
    /// <item><c>summonNovel</c> — the same summon ENTITY re-appearing (AOI blink/re-entry) is NEVER
    /// a new cast; only a fresh entity uuid (a fresh spawn) is a candidate.</item>
    /// <item><c>ownerIsKnownCombatant</c> — the AOI-entry phantom guard: a player walking INTO view
    /// with an already-active companion fires an appear that is NOT a fresh cast. Requiring the
    /// owner to already hold a _stats row (the cheapest existing signal: an O(1) hit on a dict the
    /// combat path already maintains, populated only by the owner actually dealing/healing/taking
    /// something this segment) kills that phantom. Accepted degradations, both failing toward a
    /// MISSED cast, never an invented one: a genuine cast before the owner's first combat event is
    /// skipped, and _stats clears per archive (Clear()), so a cast in the first instants after a
    /// mid-run archive may be skipped too — a damaging imagine is still caught by the damage path.</item>
    /// </list></summary>
    internal static AppearCastGate DecideAppearCast(bool summonerIsSelf, bool summonNovel, bool ownerIsKnownCombatant)
    {
        if (summonerIsSelf) return AppearCastGate.SelfSummoner;
        if (!summonNovel) return AppearCastGate.RepeatSummon;
        if (!ownerIsKnownCombatant) return AppearCastGate.OwnerNotInCombat;
        return AppearCastGate.Record;
    }

    /// <summary>Pure guard for the NN=00 synthetic probe: the config id must be positive and small
    /// enough that <c>configId * 100</c> cannot overflow int. The largest real band is the 7-digit
    /// companion one (3000033 * 100 = 300003300, fits comfortably); the guard is for garbage config
    /// ids, not real table rows. Pinned headless.</summary>
    internal static bool CanProbeImagineComposite(int configId)
        => configId > 0 && configId <= int.MaxValue / 100;

    // Appear-sourced record path. Called only for player-owned appears (gate above). Appears are
    // rare (the framework raises EntitySummonAppeared once per appear, only for summoner-attributed
    // entities), so a FRESH GetMonsterByEntity interop call is fine here — deliberately NOT routed
    // through ResolveMonsterCandidate's _bossCheck cache (Plugin.BossDetection.cs): summon uuids
    // churn with every cast and would spend slots of the bounded cache that exists to protect the
    // boss/elite admission hot path. Same underlying service call either way.
    // A null imagine probe is a graceful no-op: either the summon simply isn't an imagine's, OR the
    // running framework predates the aoyi monster closure (<= 1.18.x GetImagineForSkill has no
    // NN=00 composite path) — appear-sourced capture then degrades to recording nothing, and the
    // damage-path detector above is unaffected.
    // Recording happens ONLY after ObserveBurstHit says the (owner, base) key is quiet — which also
    // PRIMES the key, so a DAMAGING imagine detected via its appear does not re-record on its first
    // hit seconds later (and symmetrically, an appear arriving just after the first hit is deduped).
    // Timestamp: the appear instant (press-time-ish) — same anchor ResolveImagineCastMs prefers.
    private void TryRecordImagineCastFromAppear(CombatEvent.EntitySummonAppeared sa)
    {
        bool isSelf = sa.SummonerId.Value == _services.CombatSnapshot.LocalEntityId.Value;
        bool novel = !isSelf && _seenSummons.MarkSeen(sa.SummonId);   // self never spends seen-set capacity
        var gate = DecideAppearCast(isSelf, novel, _stats.ContainsKey(sa.SummonerId));
        if (gate != AppearCastGate.Record) { LogAppearCastOutcome(sa, AppearGateTag(gate), 0, 0); return; }

        int configId = _services.GameData.World.GetMonsterByEntity(sa.SummonId)?.Id ?? 0;
        if (!CanProbeImagineComposite(configId)) { LogAppearCastOutcome(sa, "no-config", configId, 0); return; }
        if (_services.ResonanceData.GetImagineForSkill(configId * 100) is not { } info)
        { LogAppearCastOutcome(sa, "not-imagine", configId, 0); return; }
        if (!ObserveBurstHit(_lastImagineHitMs, (sa.SummonerId, info.SkillId), sa.TimestampMs, ImagineRetriggerGapMs))
        { LogAppearCastOutcome(sa, "burst-dedup", configId, info.SkillId); return; }

        AddImagineCast(sa.SummonerId, info.SkillId, sa.TimestampMs);
        LogAppearCastOutcome(sa, "recorded", configId, info.SkillId);
    }

    /// <summary>Picks the cast timestamp to record for a foreign player's imagine: a recent summon-appear
    /// time when one is on file (press-time-ish, avoids the summon's wind-up), else the hit's own
    /// timestamp (the pre-existing behaviour).</summary>
    internal static long ResolveImagineCastMs(long hitMs, long? appearMs, long maxWindowMs)
    {
        if (appearMs is not { } a) return hitMs;
        if (a > hitMs) return hitMs;                  // clock-skew guard: an appear can't postdate its own hit
        if (hitMs - a > maxWindowMs) return hitMs;     // stale appear — not this burst's summon
        return a;
    }

    // ------------------------------------------------------------------------------------------------
    // Self-cast detection from LocalCooldowns (authoritative wire signal, independent of combat events).
    // ------------------------------------------------------------------------------------------------

    // Same "new cast" threshold ImagineCooldownCalc.Update applies to begin advances (jitter guard).
    internal const long SelfBeginAdvanceMs = 500;
    // First sighting of an already-old begin (plugin load / scene re-entry mid-recharge) is NOT a cast.
    internal const long SelfBeginFreshMs = 5000;

    /// <summary>True when <paramref name="beginMs"/> is a NEW cast relative to the last seen begin.</summary>
    internal static bool IsSelfCastBeginAdvance(long beginMs, long? lastBeginMs)
        => beginMs > 0 && (lastBeginMs is null || beginMs > lastBeginMs.Value + SelfBeginAdvanceMs);

    /// <summary>True when a first-sighted begin is recent enough to count as a live cast.</summary>
    internal static bool IsFreshBegin(long beginMs, long nowMs) => nowMs - beginMs <= SelfBeginFreshMs;

    // base imagine skill id -> last seen skill_begin_time. NOT cleared by Clear(): begins only ever
    // advance, so keeping them across encounter resets prevents a pre-archive cast from re-recording
    // into the next encounter as a "first sighting".
    private readonly Dictionary<int, long> _selfImagineBegin = new();

    // Poll the LocalCooldowns snapshot for imagine rows whose begin advanced (≈10 Hz from OnUpdate).
    // Runs outside the combat-event path on purpose: it captures casts made before any combat event
    // flows (pre-pull stack dumps) and while the meter window is hidden.
    private void DetectSelfImagineCasts()
    {
        if (_paused) return;
        var local = _services.CombatSnapshot.LocalEntityId;
        if (!local.IsPlayer) return;   // not in world yet
        foreach (var cd in _services.CombatSnapshot.LocalCooldowns)
        {
            if (_services.ResonanceData.GetImagineForSkill(cd.SkillId) is not { } info) continue;
            ObserveSelfImagineBegin(local, info.SkillId, cd.BeginTimeMs);
        }
    }

    private void ObserveSelfImagineBegin(EntityId local, int baseSkillId, long beginMs)
    {
        bool known = _selfImagineBegin.TryGetValue(baseSkillId, out var last);
        if (!IsSelfCastBeginAdvance(beginMs, known ? last : null)) return;
        _selfImagineBegin[baseSkillId] = beginMs;
        if (!known && !IsFreshBegin(beginMs, _services.CombatSnapshot.ServerNowMs)) return;
        AddImagineCast(local, baseSkillId, beginMs);
    }
}
