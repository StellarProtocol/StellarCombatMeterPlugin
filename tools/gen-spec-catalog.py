#!/usr/bin/env python3
"""Generate CombatMeter's spec.<subProfessionId> localization catalog entries from game data.

Owner ruling (2026-10-09, in order): show the Japanese spec name when the Stellar UI language is
Japanese; every other language shows the game's own English spec name (NOT CombatMeter's old
hard-coded short name in ProfessionSpecs.Name) with no "Spec"/"型" suffix.

Source of truth for each language:
  - EN (ko/th/id/fil share this value too): data/StarResonanceData/tables/TalentSchoolTable.json's
    `SchoolName` field, trailing " Spec" suffix stripped. Sub-profession id -> talent-school id is
    SUB_TO_SCHOOL below.

    NOTE: SUB_TO_SCHOOL maps 30001/30002 (TwinStriker) to school ids 128/129 -- NOT the 124/125 that
    framework's Stellar.Abstractions ProfessionSpecs.TalentSchool uses. TalentSchoolTable.json has
    NO id "124" (124 is only a TalentStage sub-entry of school 125, "Hand Cannon" -- unrelated), and
    125 itself is that unrelated "Hand Cannon" school. 128/129 are the real Formless/Crimson schools
    and match the owner-approved extraction in .superpowers/sdd/jp-class-spec-names.md. This looks
    like a pre-existing bug in the framework constant (out of scope for this change -- it is
    Stellar.Abstractions code, not this plugin -- but worth a framework-side look since
    ProfessionSpecs.TalentSchool also feeds school-lib gear-attr lookups for TwinStriker).

  - JA: the JP client's full spec name (harvested from the JP client's own game data, not
    translated -- see jp-class-spec-names.md), trailing "型" suffix stripped. SUB_TO_JP_FULL below
    carries the harvested full forms so the strip is mechanical and auditable, matching the English
    treatment (strip a known suffix rather than hand-typing the short form).

Usage:
    python3 tools/gen-spec-catalog.py <path-to-TalentSchoolTable.json>

    e.g. (from the stellar-devkit superproject root):
    python3 .worktrees/combatmeter-i18n-ko/tools/gen-spec-catalog.py \\
        data/StarResonanceData/tables/TalentSchoolTable.json

Rewrites the spec.<id> keys in-place in Lang/en.json, Lang/ko.json, Lang/th.json, Lang/id.json,
Lang/fil.json (all = the stripped EN game name) and Lang/ja.json (the stripped JP game name).
Every other key is left untouched; new keys are appended at the end (ascending sub-profession id)
via plain dict insertion order -- re-running updates existing spec.* values in place without
reordering them. Output is byte-for-byte consistent with the existing catalog formatting
(json.dumps(indent=2, ensure_ascii=False), insertion order preserved, no trailing newline).
"""
import json
import os
import sys

# sub-profession id -> talent-school id (TalentSchoolTable.json top-level key). See module
# docstring re: 30001/30002 diverging from the framework's ProfessionSpecs.TalentSchool.
SUB_TO_SCHOOL = {
    10001: 101, 10002: 102,
    20001: 104, 20002: 105,
    30001: 128, 30002: 129,
    40001: 107, 40002: 108,
    50001: 110, 50002: 111,
    90001: 113, 90002: 114,
    110001: 116, 110002: 117,
    120001: 122, 120002: 123,
    130001: 119, 130002: 120,
}

# sub-profession id -> JP full spec name (型 form), harvested from the JP client
# (.superpowers/sdd/jp-class-spec-names.md, owner-approved 2026-10-09). NEVER invent or translate
# these -- only the mechanical "型" suffix-strip below is applied to reach the short form.
SUB_TO_JP_FULL = {
    10001: "雷刃型", 10002: "月影型",
    20001: "氷牙型", 20002: "霜天型",
    30001: "双炎型", 30002: "炎舞型",
    40001: "烈風型", 40002: "乱風型",
    50001: "威咲型", 50002: "森癒型",
    90001: "剛身型", 90002: "剛守型",
    110001: "狼弓型", 110002: "鷹弓型",
    120001: "光砕型", 120002: "光盾型",
    130001: "狂音型", 130002: "響奏型",
}

NON_JA_LANGS = ("en", "ko", "th", "id", "fil")


def strip_suffix(name, suffix):
    if name.endswith(suffix):
        return name[: -len(suffix)].rstrip(), True
    return name, False


def build_en_values(table_path):
    with open(table_path, encoding="utf-8") as f:
        table = json.load(f)
    values = {}
    for sub, school_id in SUB_TO_SCHOOL.items():
        row = table.get(str(school_id))
        if row is None:
            sys.exit(f"error: talent school {school_id} (sub {sub}) not found in {table_path}")
        stripped, hit = strip_suffix(row["SchoolName"], " Spec")
        if not hit:
            print(f"note: school {school_id} name '{row['SchoolName']}' (sub {sub}) has no "
                  f"' Spec' suffix; using as-is", file=sys.stderr)
        values[sub] = stripped
    return values


def build_ja_values():
    values = {}
    for sub in SUB_TO_SCHOOL:
        full = SUB_TO_JP_FULL.get(sub)
        if full is None:
            sys.exit(f"error: no JP name for sub-profession {sub}")
        stripped, hit = strip_suffix(full, "型")
        if not hit:
            print(f"note: JP name '{full}' (sub {sub}) has no trailing 型; using as-is",
                  file=sys.stderr)
        values[sub] = stripped
    return values


def update_catalog(path, values):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    for sub in sorted(values):
        data[f"spec.{sub}"] = values[sub]
    with open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(data, indent=2, ensure_ascii=False))


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    table_path = sys.argv[1]
    lang_dir = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Lang"))

    en_values = build_en_values(table_path)
    ja_values = build_ja_values()

    for lang in NON_JA_LANGS:
        update_catalog(os.path.join(lang_dir, f"{lang}.json"), en_values)
    update_catalog(os.path.join(lang_dir, "ja.json"), ja_values)

    print(f"spec.<id> written for {len(SUB_TO_SCHOOL)} sub-professions "
          f"in en/ko/th/id/fil (game name) + ja (JP name) catalogs under {lang_dir}")


if __name__ == "__main__":
    main()
