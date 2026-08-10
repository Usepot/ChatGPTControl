import { execFileSync } from "node:child_process";
import {
  chmodSync,
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import process from "node:process";

const repoRoot = path.resolve(import.meta.dirname, "..");
const companionRoot = path.join(repoRoot, "apps", "electron-companion");
const projectPath = path.join(
  companionRoot,
  "avalonia",
  "PortableCodex.Desktop",
  "PortableCodex.Desktop.csproj",
);
const releaseRoot = path.join(companionRoot, "release");
const appPath = path.join(releaseRoot, "Portable Codex.app");
const contentsPath = path.join(appPath, "Contents");
const macOsPath = path.join(contentsPath, "MacOS");
const resourcesPath = path.join(contentsPath, "Resources");
const stagingPath = mkdtempSync(path.join(os.tmpdir(), "portable-codex-publish-"));
const publishPath = path.join(stagingPath, "publish");
const rid = process.env.PORTABLE_CODEX_RID ??
  (process.arch === "arm64" ? "osx-arm64" : "osx-x64");
const localDotnet = path.join(repoRoot, ".tools", "dotnet", "dotnet");
const dotnet = process.env.DOTNET ?? (existsSync(localDotnet) ? localDotnet : "dotnet");

if (process.platform !== "darwin") {
  console.error("The macOS app bundle can only be created on macOS.");
  process.exit(1);
}

const run = (command, args, options = {}) => {
  execFileSync(command, args, {
    cwd: repoRoot,
    stdio: "inherit",
    ...options,
  });
};

console.log(`Publishing Portable Codex for ${rid}...`);
run(dotnet, [
  "publish",
  projectPath,
  "-c",
  "Release",
  "-r",
  rid,
  "--self-contained",
  "true",
  "-p:PublishSingleFile=false",
  "-p:DebugType=None",
  "-p:DebugSymbols=false",
  "-o",
  publishPath,
]);

const publishedExecutable = [
  path.join(publishPath, "PortableCodex.Desktop"),
  path.join(publishPath, "PortableCodex.Desktop.exe"),
].find((candidate) => existsSync(candidate));

if (!publishedExecutable) {
  throw new Error(`dotnet publish did not produce the expected apphost in ${publishPath}`);
}

rmSync(appPath, { recursive: true, force: true });
mkdirSync(macOsPath, { recursive: true });
mkdirSync(resourcesPath, { recursive: true });

// Avalonia's Skia renderer ships native dylibs alongside the apphost. Keep the
// complete self-contained publish output inside Contents/MacOS so Finder
// launches the same artifact as `dotnet run`, without a machine-wide runtime.
cpSync(publishPath, macOsPath, { recursive: true });
const appExecutableName = path.basename(publishedExecutable);
const appExecutable = path.join(macOsPath, appExecutableName);
chmodSync(appExecutable, 0o755);

const iconSource = path.join(
  companionRoot,
  "native",
  "PortableCodex.Native",
  "Assets",
  "PortableCodex.ico",
);
const iconSetPath = path.join(stagingPath, "PortableCodex.iconset");
const icnsPath = path.join(resourcesPath, "PortableCodex.icns");
mkdirSync(iconSetPath, { recursive: true });

for (const size of [16, 32, 128, 256, 512]) {
  run("sips", ["-s", "format", "png", "-z", String(size), String(size), iconSource, "--out", path.join(iconSetPath, `icon_${size}x${size}.png`)], { stdio: "ignore" });
  run("sips", ["-s", "format", "png", "-z", String(size * 2), String(size * 2), iconSource, "--out", path.join(iconSetPath, `icon_${size}x${size}@2x.png`)], { stdio: "ignore" });
}
run("iconutil", ["-c", "icns", iconSetPath, "-o", icnsPath]);

const plist = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key>
  <string>Portable Codex</string>
  <key>CFBundleExecutable</key>
  <string>${appExecutableName}</string>
  <key>CFBundleIconFile</key>
  <string>PortableCodex</string>
  <key>CFBundleIdentifier</key>
  <string>com.openai.portablecodex</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Portable Codex</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>0.1.0</string>
  <key>CFBundleSignature</key>
  <string>????</string>
  <key>CFBundleVersion</key>
  <string>0.1.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
`;
writeFileSync(path.join(contentsPath, "Info.plist"), plist);
writeFileSync(path.join(contentsPath, "PkgInfo"), "APPL????\n");

console.log(`Created ${appPath}`);
console.log("Open it from Finder, Spotlight, or the command line with:");
console.log(`open '${appPath}'`);
