# THETA Quantower Bridge V2

This folder is a separate Quantower plugin project. It does not modify the old OMEGA plugin or the THETA application.

## What V2 provides

- Loopback-only HTTP server on `http://127.0.0.1:17843`
- Optional bearer-token authentication through `THETA_QUANTOWER_TOKEN`
- `/health`, `/symbols`, `/quote`, `/bars`, `/trades`, `/market-book`
- Server-sent realtime stream at `/stream?symbol=...&after=...`
- Quantower `NewLast` subscription with sequence numbers
- Exchange timestamp and local receive timestamp
- Last price, trade size, bid, ask, and aggressor flag when supplied by the connection
- Bounded 100,000-event buffer per symbol
- Runtime event subscription instead of a fixed list of symbols
- Historical bars through Quantower's `GetHistory` API

The V2 plugin intentionally has **no order-execution endpoint**. THETA remains the only execution boundary. This keeps every order behind the durable SQL ledger, risk gate, audit journal, and emergency kill switch. The current THETA Quantower preflight also remains fail-closed until a separately tested execution adapter exists.

## Build

Build on the same Windows computer that has the referenced Quantower version installed:

```powershell
dotnet build .\QuantowerThetaBridgeV2.csproj -c Release
```

The THETA development computer currently does not have a discoverable .NET SDK, so this source was structurally validated but not compiled here. Use `Build-QuantowerV2.ps1` on the Quantower development machine; do not install SDK assemblies into Quantower's plug-in folder.

The project references the installed SDK at:

```text
D:\Programy\Trading\Quantower\TradingPlatform\v1.146.18\bin
```

If Quantower is updated, change the two `HintPath` values in the project file to the matching installed version.

## Install

1. Stop Quantower.
2. Build the project in Release mode.
3. Create a separate folder:

```text
D:\Programy\Trading\Quantower\Settings\Scripts\plug-ins\ThetaQuantowerBridgeV2
```

4. Copy only `bin\Release\net10.0-windows\ThetaQuantowerBridgeV2.dll` into that folder.
5. Start Quantower.
6. Confirm:

```text
http://127.0.0.1:17843/health
```

Do not copy Quantower SDK DLLs beside the plugin. Quantower supplies them at runtime.

## Manual smoke tests

With a connected Quantower demo account and a known symbol:

```powershell
Invoke-RestMethod http://127.0.0.1:17843/health
Invoke-RestMethod 'http://127.0.0.1:17843/symbols'
Invoke-RestMethod 'http://127.0.0.1:17843/quote?symbol=EURUSD'
Invoke-RestMethod 'http://127.0.0.1:17843/trades?symbol=EURUSD&after=0&limit=10'
```

The stream can be tested with:

```powershell
curl.exe -N 'http://127.0.0.1:17843/stream?symbol=EURUSD&after=0'
```

The exact symbol must match the name returned by `/symbols`; futures and CFD symbols may contain broker-specific suffixes.

## Production safety

V2 does not bypass Quantower authentication, exchange entitlements, or redistribution rules. It binds to loopback only. The bounded buffer prevents an unbounded memory failure, but it is not a substitute for durable persistence; THETA should consume `/stream` or `/trades` and write normalized events through its backend batch writer.

Before enabling any connector for trading tests, the account must be positively identified as demo/paper. THETA's `LIVE_TRADING_ENABLED` setting alone must not be used to override the provider's demo-account check. Every test order must still pass the SQL reserve-before-submit ledger, risk engine, audit trail, and emergency kill switch.

## Compatibility note

The V2 plugin uses the same port and core read-only endpoints as the V1 plugin, so install only one of them at a time. V2 adds the realtime stream and sequence-aware trade buffer. If a specific Quantower connection rejects its historical `GetHistory` request, retain the V1 plugin for that connection and report the symbol/timeframe response for adapter refinement; do not fall back to frontend calculations.

## API references

- Quantower Algo extensions: https://help.quantower.com/quantower/quantower-algo/introduction
- Quantower Symbol API: https://api.quantower.com/docs/TradingPlatform.BusinessLayer.Symbol.html
- Quantower Core API: https://api.quantower.com/docs/TradingPlatform.BusinessLayer.Core.html
