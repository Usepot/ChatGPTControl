# Portable Codex Companion App

This app is native-only on Windows (WPF on .NET 8).

## Native Windows app

- `npm run build`
- `npm run dev`
- `npm run start`
- `npm run dist:win`
- `npm run native:restore`
- `npm run native:build`
- `npm run native:run`
- `npm run native:publish`

Project location:

- `native\PortableCodex.Native.sln`
- `native\PortableCodex.Native\PortableCodex.Native.csproj`

Direct .NET commands:

- `dotnet restore native\PortableCodex.Native.sln`
- `dotnet build native\PortableCodex.Native.sln -c Release`
- `dotnet run --project native\PortableCodex.Native\PortableCodex.Native.csproj`
- `dotnet publish native\PortableCodex.Native\PortableCodex.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release-native`
