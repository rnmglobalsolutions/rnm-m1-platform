# Local Development

## Setup

1. Install .NET SDK 10.0.203 or use the pinned SDK from `global.json`.
1. Copy `src/RNM.Platform.Api/local.settings.json.example` to `local.settings.json`.
2. Use Key Vault secret names only. Do not commit secret values.
3. Keep tenant and vertical behavior in `/config`.

## Current Scaffold Limits

Provider API calls are intentionally not implemented yet.
