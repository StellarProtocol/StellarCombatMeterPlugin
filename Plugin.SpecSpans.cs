using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Talent-spec upload (spec upload design 2026-09-26, items 1 + 2; framework ≥ 2.11.0). Wires
/// <see cref="SpecSpanTracker"/> to the framework's EVENT-DRIVEN <c>CombatEvent.SpecChanged</c> and bakes the
/// per-archive <c>specSpans</c>; also holds the class check for the sticky spec cache.
/// <para>CAPTURE-ONLY: nothing here feeds the archive engine, the verdict, the run id, a boss surface or the upload
/// decision — the spans ride the actor block of the summary like <c>classSpans</c>. Tracking runs through pause (a
/// spec is a tracking fact, not a displayed number — owner doctrine 2026-08-14).</para>
/// <para>TIME BASE: spans are stamped with <c>ICombatSnapshot.ServerNowMs</c> when the event is handled on the main
/// thread — the SAME clock <c>TickClassTimeline</c> stamps <c>classSpans</c> with and the archive's
/// <c>ArchivedAtMs</c> caps both — not with the event's own wire-receive <c>TimestampMs</c>, so the site can intersect
/// the two timelines directly.</para>
/// </summary>
public sealed partial class Plugin
{
    private readonly SpecSpanTracker _specSpans = new();

    /// <summary>OnCombatEvent hook (ungated by pause). Players only.</summary>
    private void ObserveSpecChanged(CombatEvent.SpecChanged sc)
    {
        if (!sc.TargetId.IsPlayer) return;
        _specSpans.OnSpecChanged(sc.TargetId.Value, sc.NewSubProfessionId, sc.FromTalent, _services.CombatSnapshot.ServerNowMs);
    }

    /// <summary>Run start (<c>TickLoadoutRunBoundary</c>, beside <c>_classSpans.ResetForRun()</c>): drop the previous
    /// run's timelines, then seed the CURRENT talent spec of every already-known player from
    /// <c>ICombatSpec.TryGetTalentSpec</c> — a player already in view at run start raises no SpecChanged. "Known" =
    /// whoever the tracker held + self + the party roster + this encounter's combatants.</summary>
    private void ResetSpecSpansForRun()
    {
        var ids = new HashSet<long>(_specSpans.Entities());
        var self = _services.CombatSnapshot.LocalEntityId;
        if (self.IsPlayer) ids.Add(self.Value);
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId != 0) ids.Add(m.EntityId.Value);
        foreach (var id in _stats.Keys)
            if (id.IsPlayer) ids.Add(id.Value);

        _specSpans.ResetForRun();
        var nowMs = _services.CombatSnapshot.ServerNowMs;
        foreach (var raw in ids)
            if (_services.CombatSpec.TryGetTalentSpec(new EntityId(raw), out var spec))
                _specSpans.Seed(raw, spec, nowMs);
    }

    /// <summary>At archive (called from <c>ApplySpecs</c>): bakes each player's talent spans, capped at
    /// <c>ArchivedAtMs</c>, into its frozen snapshot — the same run-anchored bake-in contract as
    /// <c>ApplyClassSpans</c>. An entity with no talent span keeps empty arrays (→ <c>specSpans</c> omitted).</summary>
    private void ApplySpecSpans(EncounterHistoryEntry entry)
    {
        foreach (var (id, snap) in entry.Entities)
        {
            if (!id.IsPlayer) continue;
            var spans = _specSpans.Spans(id.Value, entry.ArchivedAtMs);
            if (spans.Count > 0) WriteSpecSpansToSnapshot(snap, spans);
        }
    }

    /// <summary>Pure: writes each <c>[specId, startMs, endMs]</c> triple into the snapshot's parallel SpecSpan*
    /// arrays (fresh arrays — later tracker events never touch a frozen archive).</summary>
    internal static void WriteSpecSpansToSnapshot(EntitySnapshot snap, IReadOnlyList<long[]> spans)
    {
        snap.SpecSpanId    = new long[spans.Count];
        snap.SpecSpanStart = new long[spans.Count];
        snap.SpecSpanEnd   = new long[spans.Count];
        for (var i = 0; i < spans.Count; i++)
        {
            snap.SpecSpanId[i]    = spans[i][0];
            snap.SpecSpanStart[i] = spans[i][1];
            snap.SpecSpanEnd[i]   = spans[i][2];
        }
    }

    /// <summary>Pure rule behind <c>StickySpec</c> (design item 2): the live framework spec is served as-is; otherwise
    /// the cached last-known spec is served ONLY when its class (<c>spec / 10000</c>) is the entity's current PLAYABLE
    /// class — a Falconry cached for a player now on Beat Performer answers 0 (the row falls back to the class name).
    /// An unknown class or a Battle Imagine transform id never validates a cached spec.</summary>
    internal static int ResolveStickySpec(int liveSpec, int cached, int currentClass)
    {
        if (liveSpec > 0) return liveSpec;
        return cached > 0 && PlayableClass.IsPlayable(currentClass) && cached / 10000 == currentClass ? cached : 0;
    }

    /// <summary>Pure: the class a FROZEN snapshot's actor was playing at the archive instant, for the archive-time
    /// sticky-spec check — the LAST playable class of its baked class timeline (the snapshot's attr 220 is captured
    /// once, when the snapshot first populates, and predates a later swap), else the reported base class.</summary>
    internal static int FrozenCurrentClass(EntitySnapshot snap)
    {
        for (var i = snap.ClassSpanProf.Length - 1; i >= 0; i--)
            if (PlayableClass.IsPlayable(snap.ClassSpanProf[i])) return (int)snap.ClassSpanProf[i];
        return PlayableClass.ResolveActorProfession(snap);
    }

    /// <summary>The entity's current PLAYABLE class for the sticky-spec check: party roster, then self's live class
    /// (the container's curProfessionId — a transform does not touch it), then attr 220 (cheap single-key read),
    /// then the last playable class the row showed (a transformed player keeps their real class). Never consults the
    /// spec itself (no recursion through <c>ResolveProfessionId</c>'s spec-parent fallback).</summary>
    private int CurrentPlayableClass(EntityId id)
    {
        long charId = id.Value >> 16;
        int roster = 0;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId) { roster = m.Profession; break; }
        int live = id == _services.CombatSnapshot.LocalEntityId ? _services.Loadout.LiveState?.ProfessionId ?? 0 : 0;
        var attr = (int)_services.EntityDetail.GetAttribute(id, AttrProfessionIdForTimeline);
        _lastShownClass.TryGetValue(id.Value, out var sticky);
        return PlayableClass.ResolveDisplayProfession(sticky, roster, live, attr);
    }
}
