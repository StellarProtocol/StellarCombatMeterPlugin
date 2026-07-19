# Stellar.CombatMeter

Real-time party DPS/HPS combat overlay for [StellarResonance](https://github.com/StellarProtocol/StellarResonanceModSystem).
Builds against the published plugin SDK (`Stellar.Abstractions`, `Stellar.PluginContracts`,
`Stellar.Plugin.InteropRefs`) — no framework checkout or game install needed:

```bash
dotnet build -c Release
```

Published to the launcher via the [plugin registry](https://github.com/StellarProtocol/StellarResonancePlugins)
(manifest pins this repo + commit; CI builds it from source). AGPL-3.0-or-later.

## Hard product requirements (owner-stated, non-negotiable)

1. **The position replay covers the ENTIRE run — dungeon entry to run end.** Acceptance criterion (owner, verbatim): *"the movement suppose to be send from get inside the dungeon to archive event triggered."* Window 1 = entry → first banked archive (walk-in and opener included), then archive→archive windows; suppressed/junk archives neither upload nor advance the window; nothing resets the recording mid-run (buffers reset only at true run end). Any start/end clip in a replay is a **P0 defect** — this was reported 5+ times before the delta-window model fixed it. The tests that pin this invariant (`ReplayWindowTests`: window-concatenation-equals-baseline; suppressed-archive-no-touch) must never be weakened or deleted.
2. **A manual archive is always saved** (never suppressed) and always acknowledged (toast + `manual-press` log line). Auto archives are suppressed only when genuinely empty, defined by CONTENT alone (owner, verbatim): *"junk = when nothing happen DPS=0, HPS=0, TAKEN=0. and even I do nothing and all other player keep having DPS/HPS/TAKEN update it's not junk too."* So suppression fires iff every stat row is all-zero (DPS=HPS=Taken=0) and no fresh run result — **never** by participant-count or span; ANY nonzero row (even a lone single-participant instant hit) banks as its own entry, and anything carrying a run result/settlement saves. A suppressed archive **wipes nothing**: its accumulated rows/actors + combat state carry forward untouched into the next banked entry (so pre-fight actors seen with zero rows appear in the next entry).

Any change touching `Plugin.Replay.cs`, `Plugin.History.cs`, `Plugin.ReplayWindow.cs`, or the archive/suppression paths must re-verify both requirements explicitly.
