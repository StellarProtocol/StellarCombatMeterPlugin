namespace Stellar.CombatMeter.LogUpload;

internal static partial class CombatLogWriter
{
    // Per-track truncation flags (spec § 4.2). The dmg track's flag is `truncatedEvents` (written by the
    // caller beside it); this writes the OTHER tracks'. The retired BuffEffectSampler's `buffEffects` array
    // (2.6.0+fa95dd6) is deliberately gone — spec § 6.0/§ 6.1.
    private static void WriteTrackFlags(JsonWriter w, Derived d)
    {
        w.Name("truncatedBuffEvents").Bool(d.TruncatedBuffEvents);
    }

    /// <summary>Test-only seam: runs the `derived` writer alone into a fresh <see cref="JsonWriter"/>
    /// (no full <see cref="CombatLog"/> needed) — see <c>CombatLogWriterTrackFlagsTests</c>.</summary>
    internal static string WriteDerivedForTest(Derived d)
    {
        var w = new JsonWriter();
        WriteDerived(w, d);
        return w.ToString();
    }
}
