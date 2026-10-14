#!/usr/bin/env bash
# Builds the macOS release of UniFiles: tests, self-contained publish, bundled ffmpeg with its
# license, the app bundle (icon, Info.plist with the file types it opens), then the Velopack
# installer (.pkg) and update packages.
#
# Usage:   ./build/release-macos.sh <version> [--arch arm64|x64] [--skip-tests] [--rebuild]
#                                             [--sign-app "Developer ID Application: …"]
#                                             [--sign-installer "Developer ID Installer: …"]
#                                             [--notary-profile <notarytool profile>]
# Example: ./build/release-macos.sh 0.2.0 --arch arm64
#
# <version> is SemVer: 0.2.0, or 0.2.0-alpha for a pre-release.
# Output: artifacts/releases/osx-<arch> (UniFiles-osx-<arch>.dmg, UniFiles-osx-<arch>-Setup.pkg,
# UniFiles-osx-<arch>-Portable.zip, update packages). Run once per architecture; each has its own update channel (osx-arm64, osx-x64).
# Without a Developer ID the app is only ad-hoc signed: Gatekeeper asks users to confirm the first launch
# (System Settings > Privacy & Security > Open Anyway).
# Ghostscript isn't bundled on macOS (no official binaries): PDF compression uses a Homebrew install.
# Needs python3 (for dmgbuild, installed in build/cache on first run).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1

version=""
arch="$(uname -m | sed 's/x86_64/x64/')"
skip_tests=0
rebuild=0
# "-" is an ad-hoc signature: without it, macOS reports a downloaded app as damaged.
sign_app="-"
sign_installer=""
notary_profile=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) arch="$2"; shift 2 ;;
        --skip-tests) skip_tests=1; shift ;;
        --rebuild) rebuild=1; shift ;;
        --sign-app) sign_app="$2"; shift 2 ;;
        --sign-installer) sign_installer="$2"; shift 2 ;;
        --notary-profile) notary_profile="$2"; shift 2 ;;
        -*) echo "Unknown option: $1" >&2; exit 1 ;;
        *) version="$1"; shift ;;
    esac
done
[[ -n "$version" ]] || { echo "Usage: $0 <version> [--arch arm64|x64] …" >&2; exit 1; }
[[ "$arch" == "arm64" || "$arch" == "x64" ]] || { echo "Unknown architecture: $arch (arm64 or x64)." >&2; exit 1; }
[[ "$(uname -s)" == "Darwin" ]] || { echo "The macOS release must be built on a Mac." >&2; exit 1; }

# macOS only accepts numbers in the bundle version: 0.2.0-alpha is shown as 0.2.0.
bundle_version="${version%%-*}"
rid="osx-$arch"
root="$(cd "$(dirname "$0")/.." && pwd)"
cache="$root/build/cache"
artifacts="$root/artifacts"
publish="$artifacts/publish-$rid"
tools="$publish/tools"
releases="$artifacts/releases/$rid"

# Pinned third-party binaries: same bytes in every release (checked by SHA-256).
# Static builds by Martin Riedl (https://ffmpeg.martin-riedl.de), same FFmpeg version as Windows.
ffmpeg_version="9.0.2"
if [[ "$arch" == "arm64" ]]; then
    ffmpeg_base="https://ffmpeg.martin-riedl.de/download/macos/arm64/1789931890_9.0.2"
    ffmpeg_sha256="c8ed4c4e6978a03c485edbfe4e0a5dc2380f8a30bba5150531b31b094492d924"
    ffprobe_sha256="fcbe839537485eaee7a7a8bc5cbc0f90d53617e80943e8a5b2e31cb851197ea6"
else
    ffmpeg_base="https://ffmpeg.martin-riedl.de/download/macos/amd64/1789931006_9.0.2"
    ffmpeg_sha256="7c6b4125b191cbf773832dc51f424cf2b6bb7da43007d1e066f95909e47cacd4"
    ffprobe_sha256="2322438ed2f6319a691291b247d09c69dcaa3a982460d1f269a7e1af335cfdfd"
fi

step() { printf '\n\033[33m==> %s\033[0m\n' "$1"; }

step "Version $version ($rid)"
mkdir -p "$cache" "$artifacts"
rm -rf "$publish"
[[ $rebuild -eq 1 ]] && rm -rf "$releases"

if [[ $skip_tests -eq 0 ]]; then
    step "Tests"
    dotnet test "$root/UniFiles.slnx" -c Release --nologo -v q
fi

step "Publish (self-contained, $rid)"
dotnet publish "$root/src/UniFiles.App/UniFiles.App.csproj" -c Release -r "$rid" -o "$publish" --nologo -v q \
    -p:Version="$version" -p:InformationalVersion="$version"

step "ffmpeg $ffmpeg_version ($arch)"
mkdir -p "$tools"
for program in ffmpeg ffprobe; do
    zip="$cache/$program-$ffmpeg_version-macos-$arch.zip"
    [[ -f "$zip" ]] || curl -fsSL "$ffmpeg_base/$program.zip" -o "$zip"
    expected="$ffmpeg_sha256"
    [[ "$program" == "ffprobe" ]] && expected="$ffprobe_sha256"
    actual="$(shasum -a 256 "$zip" | cut -d' ' -f1)"
    [[ "$actual" == "$expected" ]] || { echo "$program archive checksum mismatch ($actual)." >&2; exit 1; }
    unzip -o -q "$zip" "$program" -d "$tools"
    chmod +x "$tools/$program"
done
# Built with --enable-gpl --enable-version3: GPL v3, the same text as UniFiles' own license.
cp "$root/LICENSE" "$tools/ffmpeg-LICENSE.txt"

step "Smoke test of the bundled tools"
"$tools/ffmpeg" -hide_banner -version | head -n 1
"$tools/ffprobe" -hide_banner -version | head -n 1

echo "Published size: $(du -sh "$publish" | cut -f1)"

step "App bundle resources"
bundle="$artifacts/bundle-$rid"
rm -rf "$bundle" && mkdir -p "$bundle/UniFiles.iconset"
icon="$root/src/UniFiles.App/Assets/unifiles-icon.png"
for size in 16 32 128 256 512; do
    sips -z $size $size "$icon" --out "$bundle/UniFiles.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    [[ $double -le 512 ]] && sips -z $double $double "$icon" --out "$bundle/UniFiles.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$bundle/UniFiles.iconset" -o "$bundle/UniFiles.icns"

# The file types listed here get "Open With > UniFiles" in the Finder and can be dropped on the
# Dock icon. "Alternate" rank: UniFiles never becomes the default app for them.
cat > "$bundle/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>UniFiles</string>
  <key>CFBundleDisplayName</key><string>UniFiles</string>
  <key>CFBundleIdentifier</key><string>com.uniformsystems.unifiles</string>
  <key>CFBundleExecutable</key><string>UniFiles</string>
  <key>CFBundleIconFile</key><string>UniFiles.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$bundle_version</string>
  <key>CFBundleVersion</key><string>$bundle_version</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>fr</string></array>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Uniform Systems · GNU GPL v3</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Files</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key>
      <array>
        <string>public.image</string>
        <string>com.adobe.pdf</string>
        <string>public.movie</string>
        <string>public.audio</string>
        <string>public.folder</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
PLIST
plutil -lint "$bundle/Info.plist" >/dev/null

step "Velopack package"
pack_args=(
    pack
    --packId UniFiles
    --packVersion "$version"
    --runtime "$rid"
    --channel "$rid"
    --packDir "$publish"
    --mainExe UniFiles
    --packTitle UniFiles
    --packAuthors "Uniform Systems"
    --icon "$bundle/UniFiles.icns"
    --plist "$bundle/Info.plist"
    --instLicense "$root/LICENSE"
    --outputDir "$releases"
)
notes="$root/build/release-notes/$version.md"
[[ -f "$notes" ]] && pack_args+=(--releaseNotes "$notes")
pack_args+=(--signAppIdentity "$sign_app")
[[ -n "$sign_installer" ]] && pack_args+=(--signInstallIdentity "$sign_installer")
[[ -n "$notary_profile" ]] && pack_args+=(--notaryProfile "$notary_profile")

cd "$root"
dotnet tool restore >/dev/null
dotnet vpk "${pack_args[@]}"

step "Disk image (.dmg)"
# Styled window (background, drag arrow, icon positions) laid out by dmgbuild, without Finder.
# The app comes from the portable package, already signed.
dmgbuild_env="$cache/dmgbuild-venv"
if [[ ! -x "$dmgbuild_env/bin/dmgbuild" ]]; then
    python3 -m venv "$dmgbuild_env"
    "$dmgbuild_env/bin/pip" install -q dmgbuild==1.6.7
fi
dmg_root="$artifacts/dmg-$rid"
rm -rf "$dmg_root" && mkdir -p "$dmg_root"
ditto -x -k "$releases/UniFiles-$rid-Portable.zip" "$dmg_root"
"$dmgbuild_env/bin/dmgbuild" -s "$root/build/dmg/settings.py" \
    -D app="$dmg_root/UniFiles.app" -D background="$root/build/dmg/background.png" -D icon="$bundle/UniFiles.icns" \
    UniFiles "$releases/UniFiles-$rid.dmg" >/dev/null
rm -rf "$dmg_root"
if [[ -n "$notary_profile" && "$sign_app" != "-" ]]; then
    codesign --sign "$sign_app" --timestamp "$releases/UniFiles-$rid.dmg"
    xcrun notarytool submit "$releases/UniFiles-$rid.dmg" --keychain-profile "$notary_profile" --wait
    xcrun stapler staple "$releases/UniFiles-$rid.dmg"
fi

step "Done"
ls -lh "$releases" | tail -n +2 | awk '{ printf "%-45s %8s\n", $9, $5 }'
