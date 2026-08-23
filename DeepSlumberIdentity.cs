// Deep-Slumber (Psychoscope) membership in the per-setup capture identity — owner ruling (CLAUDE.md,
// verbatim): "when any equipment change such as module,talents,equipments,slumberdream etc., and use
// have a combat with that setup it require plugin to take snapshot of it even class has no change."
//
// Own file (not another LoadoutCapture.cs partial) for two reasons: LoadoutCapture.cs is already at the
// 500-LoC standards threshold, and this is a self-contained pure projection — plain data in, plain data
// out, no accumulator state — so it is unit-testable on its own (DeepSlumberIdentityTests).

using System.Collections.Generic;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.CombatMeter;

/// <summary>
/// The SETUP-IDENTITY projection of a Deep-Slumber state: which areas are enabled and what is
/// socketed/allocated in each of their node maps. Pure and canonical — one token stream feeds BOTH the
/// equality used by <see cref="LoadoutCapture.SameSetup"/> and the digest folded into
/// <see cref="LoadoutCapture.IdentityDigest"/>, so the two can never disagree.
///
/// <para><b>What is IN the identity</b> — per (lineId, subType) variant, per area: the area id, its
/// <c>isActive</c> flag, and the three node maps as [nodeId, value] pairs (big = socketed psycho card,
/// middle = socketed item — the owner's "factor" — normal = allocated level). That is exactly "which
/// lines are enabled + what is socketed/allocated".</para>
///
/// <para><b>What is OUT, and why.</b> <c>Score</c> (<c>activateEffectScore</c>) is DERIVED from the
/// allocation — it moves only when the node maps already moved, so folding it in adds nothing but a
/// second chance to disagree. <c>SeasonLevels</c> is season PROGRESSION, not something the player
/// equips: a psychoscope level-up mid-run would otherwise mint a new "setup" for a build the player
/// never touched. (The framework's own change event is deliberately BROADER — it reports both — because
/// over-reporting there only costs a re-capture, while under-reporting loses the edit.)</para>
///
/// <para><b>Order-canonical.</b> Every level of the framework's Deep-Slumber walk iterates a zcontainer
/// map with Lua <c>pairs</c>, whose order is unspecified. Lines, areas and node pairs are all sorted
/// here, so a re-serialization of the IDENTICAL psychoscope can never read as a different setup.</para>
///
/// <para><b>Empty is NO-SIGNAL, in both directions</b> — see <see cref="HasSignal"/>.</para>
/// </summary>
internal static class DeepSlumberIdentity
{
    /// <summary>Whether <paramref name="state"/> carries a usable identity signal at all. Null means the
    /// framework's Lua bridge has not produced a read yet (no "DSLV" row); an empty <c>Lines</c> list is
    /// the shape a FAILED cultivate walk produces ("DSERR" — the exact error-silent emptiness that hid
    /// the Deep-Slumber capture twice, owner runs sea/O1jJepsgKC and sea/oO0MvJ4XkP). Neither is "the
    /// player has no psychoscope", so neither may mint or block a setup — the same empty-is-no-signal
    /// rule <see cref="LoadoutCapture.ImaginesDiffer"/> already applies to Battle Imagines, and the same
    /// rule that lets an empty→populated heal refresh a setup in place instead of duplicating it.</summary>
    internal static bool HasSignal(DeepSlumberState? state) => state is { Lines.Count: > 0 };

    /// <summary>Whether Deep-Slumber contributes a genuine "different setup" signal. False whenever
    /// either side lacks signal (<see cref="HasSignal"/>), so an unread/failed psychoscope can never
    /// itself mint a setup — nor stop a real gear/module/talent difference from minting one.</summary>
    internal static bool Differs(DeepSlumberState? a, DeepSlumberState? b)
    {
        if (ReferenceEquals(a, b)) return false;
        if (!HasSignal(a) || !HasSignal(b)) return false;
        return !SameTokens(Tokens(a!), Tokens(b!));
    }

    /// <summary>Folds the canonical identity tokens of <paramref name="state"/> into
    /// <paramref name="hash"/> via <paramref name="fold"/>. A no-signal state folds NOTHING, so the
    /// digest moves in lockstep with <see cref="Differs"/> — including across an unread→read heal, where
    /// neither reports a change.</summary>
    internal static void FoldInto(ref uint hash, DeepSlumberState? state, Folder fold)
    {
        if (!HasSignal(state)) return;
        foreach (var token in Tokens(state!))
        {
            fold(ref hash, (uint)token);
            fold(ref hash, (uint)(token >> 32));
        }
    }

    /// <summary>The hash-folding step supplied by the caller (<see cref="LoadoutCapture"/> owns the
    /// FNV-1a implementation; this type owns only the canonical token order).</summary>
    internal delegate void Folder(ref uint hash, uint value);

    // The canonical token stream. Counts are emitted alongside the payloads so a shorter list can never
    // be a prefix-match of a longer one.
    private static List<long> Tokens(DeepSlumberState state)
    {
        var lines = new List<DeepSlumberLine>(state.Lines);
        lines.Sort(static (x, y) => x.LineId != y.LineId ? x.LineId.CompareTo(y.LineId) : x.SubType.CompareTo(y.SubType));

        var tokens = new List<long>();
        tokens.Add(lines.Count);
        foreach (var line in lines)
        {
            tokens.Add(line.LineId);
            tokens.Add(line.SubType);
            var areas = new List<DeepSlumberArea>(line.Areas);
            areas.Sort(static (x, y) => x.AreaId.CompareTo(y.AreaId));
            tokens.Add(areas.Count);
            foreach (var area in areas)
            {
                tokens.Add(area.AreaId);
                tokens.Add(area.IsActive ? 1 : 0);
                AppendPairs(tokens, area.BigNodes);
                AppendPairs(tokens, area.MiddleNodes);
                AppendPairs(tokens, area.NormalNodes);
            }
        }
        return tokens;
    }

    private static void AppendPairs(List<long> tokens, IReadOnlyList<int[]> pairs)
    {
        var sorted = new List<int[]>(pairs.Count);
        foreach (var pair in pairs) if (pair.Length >= 2) sorted.Add(pair);
        sorted.Sort(static (x, y) => x[0] != y[0] ? x[0].CompareTo(y[0]) : x[1].CompareTo(y[1]));
        tokens.Add(sorted.Count);
        foreach (var pair in sorted) { tokens.Add(pair[0]); tokens.Add(pair[1]); }
    }

    private static bool SameTokens(List<long> a, List<long> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
