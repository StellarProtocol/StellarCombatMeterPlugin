using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stellar.Abstractions.Domain.GameData;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the owner-approved spec-name i18n (2026-10-09): CombatMeter shows the JP spec name (from
/// the JP client, no trailing "型" per the owner's final ruling) only when the Stellar UI language
/// is Japanese; every other language shows the game's own English spec name, sourced from
/// TalentSchoolTable.json with no trailing "Spec" either — NOT CombatMeter's old hard-coded
/// <see cref="ProfessionSpecs.Name"/> short label (e.g. "Iaido" / "Formless Expertise"), which
/// stays only as the fallback for a missing/unrecognised catalog key (Plugin.SpecLabel.cs).
///
/// Regenerate the spec.&lt;id&gt; catalog entries with
/// <c>tools/gen-spec-catalog.py &lt;path-to-TalentSchoolTable.json&gt;</c> if the game table ever
/// changes, and update the EXPECTED tables below to match — never "fix" this test to match a
/// hand-edited catalog value instead; it exists to pin the game-data source of truth.
/// </summary>
public sealed class SpecLabelCatalogTests
{
    // The 18 sub-profession ids ProfessionSpecs.cs knows. Its SubProfessionNames dictionary has no
    // public enumerator, so this list is hand-kept in sync with it; Known_sub_profession_ids_are_
    // still_recognised_by_ProfessionSpecs fails loudly the moment that drifts.
    private static readonly int[] KnownSubProfessionIds =
    {
        10001, 10002, 20001, 20002, 30001, 30002, 40001, 40002, 50001, 50002,
        90001, 90002, 110001, 110002, 120001, 120002, 130001, 130002,
    };

    // Expected EN spec label (ko/th/id/fil carry this exact same game-sourced value): the
    // TalentSchoolTable.json SchoolName for that spec's talent school, trailing " Spec" stripped
    // (owner 2026-10-09: "for english-> no need spec suffix"). NOTE: sub 30001/30002 (TwinStriker)
    // resolve through talent-school ids 128/129 here — NOT the 124/125 that the framework's
    // ProfessionSpecs.TalentSchool uses. TalentSchoolTable.json has no id "124" at all, and id 125
    // is an unrelated "Hand Cannon" school; 128/129 are the real Formless/Crimson schools (see
    // tools/gen-spec-catalog.py's module docstring — this looks like a pre-existing framework bug,
    // out of scope here).
    private static readonly Dictionary<int, string> ExpectedGameEnglish = new()
    {
        { 10001, "Iaido Slash" }, { 10002, "Moonstrike" },
        { 20001, "Icicle" }, { 20002, "Frostbeam" },
        { 30001, "Formless" }, { 30002, "Crimson" },
        { 40001, "Vanguard" }, { 40002, "Skyward" },
        { 50001, "Smite" }, { 50002, "Lifebind" },
        { 90001, "Earthfort" }, { 90002, "Block" },
        { 110001, "Wildpack" }, { 110002, "Falconry" },
        { 120001, "Recovery" }, { 120002, "Shield" },
        { 130001, "Dissonance" }, { 130002, "Concerto" },
    };

    // Expected JA spec label: the JP client's own spec name (harvested — .superpowers/sdd/
    // jp-class-spec-names.md, owner-approved 2026-10-09), trailing "型" stripped (owner: "i prefer
    // without spec for jp version too"). 30001's "双炎" is a DERIVED short form — the harvested JP
    // pool only carries the full "双炎型"; every other entry here exists standalone in that pool.
    private static readonly Dictionary<int, string> ExpectedJapanese = new()
    {
        { 10001, "雷刃" }, { 10002, "月影" },
        { 20001, "氷牙" }, { 20002, "霜天" },
        { 30001, "双炎" }, { 30002, "炎舞" },
        { 40001, "烈風" }, { 40002, "乱風" },
        { 50001, "威咲" }, { 50002, "森癒" },
        { 90001, "剛身" }, { 90002, "剛守" },
        { 110001, "狼弓" }, { 110002, "鷹弓" },
        { 120001, "光砕" }, { 120002, "光盾" },
        { 130001, "狂音" }, { 130002, "響奏" },
    };

    private static readonly string[] NonJaCatalogs = { "en", "ko", "th", "id", "fil" };

    [Fact]
    public void Known_sub_profession_ids_are_still_recognised_by_ProfessionSpecs()
    {
        foreach (var sub in KnownSubProfessionIds)
            Assert.True(ProfessionSpecs.Name(sub) is { Length: > 0 },
                $"sub {sub} is no longer recognised by ProfessionSpecs.Name -- update " +
                "KnownSubProfessionIds and the catalogs (tools/gen-spec-catalog.py) together with " +
                "any ProfessionSpecs change.");
    }

    [Theory]
    [MemberData(nameof(NonJaCatalogIds))]
    public void NonJa_catalog_spec_label_is_the_game_English_name(string lang, int sub)
    {
        var catalog = LoadCatalog(lang);
        Assert.True(catalog.TryGetValue($"spec.{sub}", out var actual), $"{lang}.json missing spec.{sub}");
        Assert.Equal(ExpectedGameEnglish[sub], actual);
    }

    [Theory]
    [MemberData(nameof(AllSubIds))]
    public void Ja_catalog_spec_label_is_the_JP_game_name(int sub)
    {
        var catalog = LoadCatalog("ja");
        Assert.True(catalog.TryGetValue($"spec.{sub}", out var actual), $"ja.json missing spec.{sub}");
        Assert.Equal(ExpectedJapanese[sub], actual);
    }

    public static IEnumerable<object[]> AllSubIds()
    {
        foreach (var sub in KnownSubProfessionIds) yield return new object[] { sub };
    }

    public static IEnumerable<object[]> NonJaCatalogIds()
    {
        foreach (var lang in NonJaCatalogs)
            foreach (var sub in KnownSubProfessionIds)
                yield return new object[] { lang, sub };
    }

    // Reads the catalog from CombatMeter's own embedded Lang/<lang>.json resource (the same file
    // the framework auto-discovers at load) rather than from disk — this test must pass whether
    // or not the plugin repo is checked out inside the devkit superproject (CI clones this repo
    // standalone, with no sibling data/ or docs/ directory to read a file path from).
    private static Dictionary<string, string> LoadCatalog(string lang)
    {
        var asm = typeof(Plugin).Assembly;
        var suffix = $"Lang.{lang}.json";
        string? resourceName = null;
        foreach (var name in asm.GetManifestResourceNames())
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { resourceName = name; break; }
        Assert.True(resourceName != null, $"no embedded {suffix} resource found in {asm.FullName}");

        using var stream = asm.GetManifestResourceStream(resourceName!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                result[prop.Name] = prop.Value.GetString() ?? "";
        return result;
    }
}
