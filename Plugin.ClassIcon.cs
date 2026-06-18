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

    private int ResolveProfessionId(EntityId id)
    {
        long charId = id.Value >> 16;
        // Only trust a REAL profession from the roster. A sparsely-synced party slot
        // (FastSync hp/position only, no SocialSync) carries Profession 0 — returning
        // that would blank the crest even when IPlayerState knows our own class.
        foreach (var m in _services.PartyRoster.Members)
        {
            if (m.CharId == charId && m.Profession > 0) return m.Profession;
        }
        if (id == _services.CombatSnapshot.LocalEntityId)
        {
            return _services.PlayerState.Profession;
        }
        return 0;
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
