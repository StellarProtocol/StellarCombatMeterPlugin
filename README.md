# Stellar.CombatMeter

Real-time party DPS/HPS combat overlay for [StellarResonance](https://github.com/StellarProtocol/StellarResonanceModSystem).
Builds against the published plugin SDK (`Stellar.Abstractions`, `Stellar.PluginContracts`,
`Stellar.Plugin.InteropRefs`) — no framework checkout or game install needed:

```bash
dotnet build -c Release
```

Published to the launcher via the [plugin registry](https://github.com/StellarProtocol/StellarResonancePlugins)
(manifest pins this repo + commit; CI builds it from source). AGPL-3.0-or-later.
