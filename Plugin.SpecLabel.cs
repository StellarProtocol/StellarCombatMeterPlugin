// Plugin.SpecLabel.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

// On-screen spec label i18n (owner ruling 2026-10-09): the Japanese UI language shows the game's
// own JP spec name (Lang/ja.json); every other UI language shows the game's own English spec name
// (Lang/en.json's spec.<sub>, generated from TalentSchoolTable.json by tools/gen-spec-catalog.py)
// instead of ProfessionSpecs.Name's hard-coded short label. Only the ROW DISPLAY sites below use
// this — Plugin.DiscordWebhook.cs keeps its own always-English lookup (EnglishSpecLabel) because
// outbound Discord text must not follow the viewer's Stellar UI language, and nothing else
// (uploads, persisted history, the archive engine) carries a spec NAME at all — they carry only
// the numeric sub-profession id, untouched by this file.
public sealed partial class Plugin
{
    // Per-row list rebuilds (Plugin.List.cs RebuildListRows → BuildRowData → SpecLine) run every
    // frame the window is shown, so an uncached _loc.T + fallback per row per frame would be
    // wasted string-building work. Keyed by (sub, language) — not just sub — so a stale value from
    // a missed invalidation can never leak into the wrong language; OnLanguageChanged
    // (Plugin.Localization.cs) also clears the whole cache outright on every live language switch.
    private readonly Dictionary<(int Sub, string Lang), string> _specLabelCache = new();

    /// <summary>Localized on-screen spec label for the active UI language, or <c>null</c> when the
    /// sub-profession id is unrecognised by both the catalog and <see cref="ProfessionSpecs"/>.</summary>
    private string? SpecLabel(int sub)
    {
        var lang = _loc.Language;
        var cacheKey = (sub, lang);
        if (_specLabelCache.TryGetValue(cacheKey, out var cached)) return cached;

        // ILocalization.T resolves active → English → the key literal (ILocalization.cs); a key
        // literal coming back unchanged means neither catalog has it (including sub == 0 or any
        // id outside the 18 known specs), so fall back to the framework's hard-coded short name.
        var key = $"spec.{sub}";
        var resolved = _loc.T(key);
        var label = resolved != key ? resolved : ProfessionSpecs.Name(sub);
        if (label is { Length: > 0 }) _specLabelCache[cacheKey] = label;
        return label;
    }

    // Always-English spec label for outbound text (Plugin.DiscordWebhook.cs) that must read in
    // English regardless of the viewer's Stellar UI language (owner 2026-10-09). ILocalization.T
    // always resolves through the ACTIVE language, so it can't serve this — this reads the
    // plugin's own embedded Lang/en.json resource directly instead (the same file the framework
    // already auto-discovers for the UI catalog; this is a second, independent parse of it).
    // Parsed once with JsonDocument — no reflection, unlike System.Text.Json's generic
    // JsonSerializer.Deserialize&lt;T&gt;, which HistoryJson.cs notes is AOT-stripped under
    // IL2CPP — and cached for the plugin's lifetime; the embedded resource never changes at
    // runtime.
    private static Dictionary<string, string>? _enSpecCatalogCache;

    private static string? EnglishSpecLabel(int sub)
    {
        var catalog = _enSpecCatalogCache ??= LoadEnSpecCatalog();
        return catalog.TryGetValue($"spec.{sub}", out var v) ? v : ProfessionSpecs.Name(sub);
    }

    private static Dictionary<string, string> LoadEnSpecCatalog()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var asm = typeof(Plugin).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.EndsWith("Lang.en.json", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                using var reader = new StreamReader(s);
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.StartsWith("spec.", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.String)
                        result[prop.Name] = prop.Value.GetString() ?? "";
                break;
            }
        }
        catch { /* best-effort — EnglishSpecLabel falls back to ProfessionSpecs.Name per id on a miss */ }
        return result;
    }
}
