# Portable Codex

Portable Codex lets a ChatGPT custom app, built with the Apps SDK/MCP connector flow, act on trusted local workspaces through a hosted relay and a desktop companion app. Legacy Custom GPT Actions are still supported.

## Pieces

- `apps/hosted-relay`
  Public MCP endpoint (`/mcp`), REST API, and WebSocket broker. ChatGPT Apps call `/mcp`; legacy GPT Actions call the REST tool routes.
- `apps/electron-companion`
  Native Windows companion (WPF / .NET 8) that connects outbound to the relay, runs file tools locally, prompts for gated writes, and logs every tool call. The folder name is historical; the implementation is fully native.
- `packages/shared`
  Shared protocol types for tool requests, responses, logs, and relay messages.
- `docs/openapi.yaml`
  OpenAPI spec for the legacy Custom GPT Action.
- `docs/custom-gpt-instructions.md`
  Project-maintained instruction block for the legacy Custom GPT. In development, the companion and relay read this file live; published builds use the embedded copy.

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

After changing **`docs/openapi.yaml`**, run **`npm run docs:openapi`** first so generated Action schemas stay current. The root `build`, `build:exe`, `dist:win`, and `dev:relay` scripts run this automatically.

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

## ChatGPT App Wiring (Apps SDK / MCP)

1. Start the companion and expose the relay through an HTTPS URL that ChatGPT can reach. Tailscale Funnel is the default helper for this.
2. In ChatGPT, enable developer mode, open **Settings → Connectors → Create**, and create a custom connector for Portable Codex.
3. Set the connector URL to the relay MCP endpoint, for example **`https://<device>.<tailnet>.ts.net/mcp`**. The hosted relay and local relay both serve this endpoint.
4. Configure authentication as bearer token auth and paste the companion-generated ChatGPT bearer token.
5. Save the connector. The MCP server advertises the existing Portable Codex tools through `tools/list` and forwards `tools/call` requests through the same trusted companion/device broker as the REST Action API.
6. In the companion app, add one or more trusted workspaces.
7. Have ChatGPT call `list_trusted_workspaces` before using file tools so it can pick a valid `workspaceRoot`.

The MCP endpoint is authenticated and uses the same local trust boundaries as the legacy Action API: the relay never touches files, and writes/commands still prompt on the companion device unless that toggle is disabled.

## Legacy Custom GPT Action Wiring

1. `docs/openapi.yaml` now carries a placeholder server URL. Keep the schema content there, but do not rely on the checked-in server URL value for production.
2. Run **`npm run docs:openapi`** so `docs/openapi.actions.json` / `.yaml` / `.min.json` stay in sync after any edit. Build and relay dev scripts run this automatically, but run it directly when you want to commit regenerated schema files without building.
3. Create a Custom GPT and add an Action using the relay-hosted spec URL such as **`https://<device>.<tailnet>.ts.net/openapi.yaml`** or **`https://<device>.<tailnet>.ts.net/docs/openapi.yaml`**, or paste the app-generated JSON from the companion UI. The hosted relay and local relay rewrite `servers[0].url` to the incoming public origin automatically, and both serve the latest generated schema on each request. The spec is **OpenAPI 3.1.0** in the shape Custom GPT validation accepts (`components.schemas` as plain objects, **`additionalProperties`**, **`$ref` only where needed**). Each tool sets **`x-openai-isConsequential: false`** so ChatGPT treats calls as non-consequential; **trust and approvals still run on the companion device**. Request bodies use **`workspaceRoot`** (from `list_trusted_workspaces`), not a generic `workspace` field.
4. Configure bearer auth in the Action using the relay API token.
5. Paste `docs/custom-gpt-instructions.md` into the Custom GPT instructions. The same text is also served at `/docs/custom-gpt-instructions.md`, and the Action schema includes `get_gpt_instructions` so the GPT can refresh project-maintained guidance when that tool is available.
6. In the companion app, set the matching relay URL, device ID, and device token, then add one or more trusted workspaces.
7. Have the GPT call `list_trusted_workspaces` before using the file tools so it can pick a valid `workspaceRoot`.

## First Launch

On first launch, the companion app generates a secure:

- device ID
- device token
- ChatGPT bearer token

Use the Setup panel to:

1. Copy the relay environment block.
2. Start the hosted relay with those exact values.
3. Enter the relay URL in Settings.
4. Add a trusted workspace.
5. Connect the ChatGPT custom app to the `/mcp` endpoint, or copy the GPT instructions into a legacy Custom GPT.
6. ChatGPT should call `list_trusted_workspaces` to discover roots instead of relying on a pasted workspace path.

The app will keep showing the next required step until the relay is connected and a workspace is trusted.

## Tooling Notes

- v1 exposes file tools, `run_command`, plus `list_trusted_workspaces` for workspace discovery over both MCP and the legacy REST Action API.
- Reads, search, and stat run automatically inside trusted workspaces.
- `run_command` plus Codex-compatible aliases (`shell`, `exec_command`, `shell_command`) provide the shell path for inspection, builds, and tests.
- Shell tools accept Codex-style aliases such as `cmd` / `commandLine`, `working_directory`, `timeout_ms`, `max_output_bytes`, and one-shot `stdin` / `input`.
- `view_image` loads a local image from a trusted workspace and returns a base64 data URL.
- `exec_command` can keep a Codex-style session alive and return `session_id`; `write_stdin` sends `chars` to that session or polls with empty `chars`.
- `request_permissions` is a compatibility shim; dynamic sandbox escalation is not available in the relay architecture.
- `apply_patch` accepts Codex-style multi-file patch strings while keeping the older literal replacement mode.
- Writes, patches, deletes, and commands prompt by default, with a UI toggle to disable prompts.
- The relay never touches the filesystem. Only the companion app does.

## Verification

Run the test suite:

```powershell
npm test
```
