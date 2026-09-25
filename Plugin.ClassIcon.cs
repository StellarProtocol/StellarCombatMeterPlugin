using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Class-icon rendering and pre-warming. Extracted from Plugin.cs so the main
// file stays under the 500-LoC cap. Icons are loaded via _services.GameAssets
// (the IGameAssets toolkit service backed by GameAssetsService in Infrastructure).
public sealed partial class Plugin
{
    // One-shot warm flag. Once the profession table is loaded (signaled by
    // GetProfession(1) returning non-null), we kick off async icon loads for
    // all 11 profession IDs so the icons are ready by the time the first
    // combat event populates a card.
    private bool _iconsWarmed;

    // Last PLAYABLE class shown per entity (EntityId.Value) — what a transformed row keeps showing while
    // every live source reads the Battle Imagine transform. Display only; never feeds capture/upload.
    private readonly System.Collections.Generic.Dictionary<long, int> _lastShownClass = new();

    // The row's class for display (name, crest, role colour, Class bar colour) — always a PLAYABLE class,
    // never a Battle Imagine transform (owner 2026-09-25: show the original class; PlayableClass).
    private int ResolveProfessionId(EntityId id)
    {
        long charId = id.Value >> 16;
        // 1. Real profession from the roster (SocialSync). A sparsely-synced party slot
        //    (FastSync hp/position only, no SocialSync) carries Profession 0.
        int roster = 0;
        foreach (var m in _services.PartyRoster.Members)
        {
            if (m.CharId == charId) { roster = m.Profession; break; }
        }
        // 2. Self: the framework's live class (the container's curProfessionId — a transform does not
        //    touch it), then attr 220 (which DOES flip to the transform id while transformed).
        int live = 0, attr = 0;
        if (id == _services.CombatSnapshot.LocalEntityId)
        {
            live = _services.Loadout.LiveState?.ProfessionId ?? 0;
            attr = _services.PlayerState.Profession;
        }
        _lastShownClass.TryGetValue(id.Value, out var sticky);
        var prof = PlayableClass.ResolveDisplayProfession(0, roster, live, attr);
        // 3. Fallback — derive the parent profession from the CAST-INFERRED sub-profession (spec),
        //    the same source that resolves the row's spec name. A sub-profession id encodes its parent
        //    as <ProfessionId>_00_<SpecIndex> (ProfessionSpecs), i.e. parent = subId / 10000. Without
        //    this, a party member whose SocialSync profession never arrived (open-world / freshly-joined
        //    party) rendered the DPS-red default bar + a blank crest even though their spec — and thus
        //    their class — was already known from their casts (owner-reported red/iconless meter).
        //    Evaluated lazily — only when no direct source is playable.
        if (prof == 0) prof = PlayableClass.ResolveDisplayProfession(sticky, RoleClassifier.ParentProfession(ResolveSpec(id)));
        if (prof != 0 && prof != sticky) _lastShownClass[id.Value] = prof;
        return prof;
    }

    // Update-tick hook. First warms the cache once the profession table is
    // ready, then pumps the per-slot polling each frame until every slot
    // reaches Loaded or Failed. Cheap: 11 dict lookups per frame.
    private void PumpClassIcons()
    {
        if (!_iconsWarmed)
        {
            if (_services.GameData.Combat.GetProfession(1) is null) return;
            _iconsWarmed = true;
            for (int id = 1; id <= 11; id++)
            {
                _services.GameAssets.LoadProfessionIcon(id);
            }
            return;
        }
        for (int id = 1; id <= 11; id++)
        {
            _services.GameAssets.LoadProfessionIcon(id);
        }
    }
}
