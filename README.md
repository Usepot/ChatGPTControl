# Portable Codex

Portable Codex lets a ChatGPT Custom GPT act on trusted local workspaces through a hosted relay and a desktop companion app.

## Pieces

- `apps/hosted-relay`
  Public REST API plus WebSocket broker. GPT Actions call this service.
- `apps/electron-companion`
  Native Windows companion (WPF / .NET 8) that connects outbound to the relay, runs file tools locally, prompts for gated writes, and logs every tool call. The folder name is historical; the implementation is fully native.
- `packages/shared`
  Shared protocol types for tool requests, responses, logs, and relay messages.
- `docs/openapi.yaml`
  OpenAPI spec for the Custom GPT Action.
- `docs/custom-gpt-instructions.md`
  Starter instruction block for the Custom GPT.

## Defaults

The repo ships with demo defaults so the flow is testable locally:

- Relay URL: `http://127.0.0.1:8787`
- API token: `demo-api-key`
- Device ID: `demo-device`
- Device token: `demo-device-token`

Override these in production with relay environment variables and the companion app settings UI.

## Run

Install dependencies:

```powershell
npm install
```

Build everything:

```powershell
npm run build
```

Start the relay:

```powershell
npm run dev:relay
```

Start the native companion:

```powershell
npm run dev
```

Same as `npm run start`. Legacy script names that still run the native app:

```powershell
npm run electron:dev
```

Build a standalone Windows `.exe` (self-contained, single-file x64):

```powershell
npm run build:exe
```

After changing **`docs/openapi.yaml`**, run **`npm run docs:openapi`** first so the companion’s setup screen embeds the latest Action schema.

Same publish as `npm run dist:win`. Output:

- `apps/electron-companion/release-native/PortableCodex.exe`

## Relay Configuration

The relay reads these environment variables:

- `PORT`
  HTTP port. Default: `8787`
- `RELAY_REQUEST_TIMEOUT_MS`
  Request timeout before the relay returns `timeout`. Default: `300000`
- `RELAY_API_KEYS`
  Comma-separated entries in `userId:token:defaultDeviceId` format
- `RELAY_DEVICE_TOKENS`
  Comma-separated entries in `deviceId:token` format

Example:

```powershell
$env:RELAY_API_KEYS="alice:super-secret-api-token:alice-laptop"
$env:RELAY_DEVICE_TOKENS="alice-laptop:desktop-device-token"
npm run dev:relay
```

## GPT Wiring

1. `docs/openapi.yaml` now carries a placeholder server URL. Keep the schema content there, but do not rely on the checked-in server URL value for production.
2. Run **`npm run docs:openapi`** so `docs/openapi.actions.json` / `.yaml` / `.min.json` stay in sync after any edit.
3. Create a Custom GPT and add an Action using the relay-hosted spec URL such as **`https://<device>.<tailnet>.ts.net/openapi.yaml`** or **`https://<device>.<tailnet>.ts.net/docs/openapi.yaml`**, or paste the app-generated JSON from the companion UI. The hosted relay rewrites `servers[0].url` to the incoming public origin automatically. The spec is **OpenAPI 3.1.0** in the shape Custom GPT validation accepts (`components.schemas` as plain objects, **`additionalProperties`**, **`$ref` only where needed**). Each tool sets **`x-openai-isConsequential: false`** so ChatGPT treats calls as non-consequential; **trust and approvals still run on the companion device**. Request bodies use **`workspaceRoot`** (from `list_trusted_workspaces`), not a generic `workspace` field.
4. Configure bearer auth in the Action using the relay API token.
5. Paste `docs/custom-gpt-instructions.md` into the Custom GPT instructions.
6. In the companion app, set the matching relay URL, device ID, and device token, then add one or more trusted workspaces.
7. Have the GPT call `list_trusted_workspaces` before using the file tools so it can pick a valid `workspaceRoot`.

## First Launch

On first launch, the companion app generates a secure:

- device ID
- device token
- GPT bearer token

Use the Setup panel to:

1. Copy the relay environment block.
2. Start the hosted relay with those exact values.
3. Enter the relay URL in Settings.
4. Add a trusted workspace.
5. Copy the GPT instructions into your Custom GPT.
6. The GPT should call `list_trusted_workspaces` to discover roots instead of relying on a pasted workspace path.

The app will keep showing the next required step until the relay is connected and a workspace is trusted.

## Tooling Notes

- v1 exposes file tools, `run_command`, plus `list_trusted_workspaces` for workspace discovery.
- Reads, search, and stat run automatically inside trusted workspaces.
- `run_command` provides the Codex-style shell path for inspection, builds, and tests.
- `apply_patch` accepts Codex-style multi-file patch strings while keeping the older literal replacement mode.
- Writes, patches, deletes, and commands prompt by default, with a UI toggle to disable prompts.
- The relay never touches the filesystem. Only the companion app does.

## Verification

Run the test suite:

```powershell
npm test
```
