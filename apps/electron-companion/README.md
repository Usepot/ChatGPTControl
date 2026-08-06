# Portable Codex Companion App

The companion now has a shared `net8.0` core and an Avalonia desktop shell for Windows and macOS. The original WPF app remains available as a Windows fallback.

## Native Windows app

- `npm run build:win`
- `npm run dev:win`
- `npm run native:run`
- `npm run dist:win`
- `npm run native:restore`
- `npm run native:build`
- `npm run native:run`
- `npm run native:publish`

## Cross-platform companion

- `npm run build`
- `npm run dev`
- `npm run start`
- `npm run core:build`
- `npm run desktop:build`
- `npm run desktop:run`
- `npm run desktop:publish:win`
- `npm run desktop:publish:mac`
- `npm run desktop:publish:mac:x64`
- `npm run test:core`

The Avalonia shell intentionally focuses on the always-on companion workflow: relay status, trusted workspaces, write approvals, activity, and Tailscale Funnel setup. Desktop capture/click and the embedded ChatGPT WebView remain Windows-only capabilities until their platform adapters are implemented.

On macOS, the Tailscale **Install** action opens Tailscale’s official standalone installer. Sign-in, system-extension approval, and Funnel approval remain guided steps because those OS authorizations cannot be safely automated by the companion.

Project location:

- `native/PortableCodex.Native.sln`
- `native/PortableCodex.Native/PortableCodex.Native.csproj`
- `core/PortableCodex.Core/PortableCodex.Core.csproj`
- `avalonia/PortableCodex.Desktop/PortableCodex.Desktop.csproj`

Direct .NET commands:

- `dotnet restore native/PortableCodex.Native.sln`
- `dotnet build native/PortableCodex.Native.sln -c Release`
- `dotnet run --project native/PortableCodex.Native/PortableCodex.Native.csproj`
- `dotnet publish native/PortableCodex.Native/PortableCodex.Native.csproj -c Release -r win-x64 -o release-native`

Cross-platform projects:

- `dotnet build core/PortableCodex.Core/PortableCodex.Core.csproj -c Release`
- `dotnet run --project avalonia/PortableCodex.Desktop/PortableCodex.Desktop.csproj`
