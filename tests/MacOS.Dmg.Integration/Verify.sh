#!/usr/bin/env bash
# MAC-DMG-1 macOS .dmg 集成验证：真实 hdiutil 链路 生成→attach→断言卷内容→detach→verify。
# 用法: bash tests/MacOS.Dmg.Integration/Verify.sh
# 需要 macOS 宿主与 dotnet SDK；产物仅在 artifacts/macos-dmg-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/macos-dmg-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerMacDmgIntegrationFixture.csproj"
package_cache="$integration_root/nuget-cache"
mount_root="$integration_root/mount"
identity="BundlerMacOSDmgIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

mounted_volume=""
cleanup() {
    if [[ -n "$mounted_volume" ]] && [[ -d "$mounted_volume" ]]; then
        hdiutil detach "$mounted_volume" -force >/dev/null 2>&1 || true
    fi
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Darwin" ]] || fail "This integration test requires a macOS host."
for tool in dotnet hdiutil plutil unzip python3; do
    command -v "$tool" >/dev/null || fail "$tool is unavailable on this host."
done

version="$(sed -n 's:.*<BundlerPackageVersion>\(.*\)</BundlerPackageVersion>.*:\1:p' "$repo_root/Directory.Build.props" | head -n1 | tr -d '[:space:]')"
[[ -n "$version" ]] || fail "BundlerPackageVersion is missing from Directory.Build.props."
log "package version: $version"

if [[ -e "$integration_root" ]]; then
    if [[ ! -f "$integration_root/.bundler-identity" ]] || ! grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        fail "$integration_root already exists and was not created by this script; refusing to touch it."
    fi
fi
mkdir -p "$integration_root"
printf '%s\n' "$identity" > "$integration_root/.bundler-identity"

log "== building repository packages =="
dotnet build "$repo_root/Bundler.slnx" -c Release >/dev/null
dotnet pack "$repo_root/Bundler.slnx" -c Release -o "$package_dir" >/dev/null
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.MacApp DotNet.Bundler.MacDmg; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 dmg 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.MacDmg.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the MacDmg backend assembly."

log "== publishing fixture (BundlerFormats=dmg) =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle" \
    --packages "$package_cache" >/dev/null

app_bundle="$integration_root/bundle/osx-arm64/app/Bundler Mac DMG Fixture.app"
dmg_path="$integration_root/bundle/osx-arm64/dmg/Bundler Mac DMG Fixture.dmg"
[[ -d "$app_bundle" ]] || fail "The planner did not produce the intermediate .app: $app_bundle"
[[ -f "$dmg_path" ]] || fail "The .dmg artifact is missing: $dmg_path"
plutil -lint "$app_bundle/Contents/Info.plist" >/dev/null || fail "Intermediate .app Info.plist is invalid."
log "produced: $dmg_path"

log "== mounting the .dmg and asserting volume contents =="
mkdir -p "$mount_root"
hdiutil attach "$dmg_path" -nobrowse -readonly -mountpoint "$mount_root" >/dev/null \
    || fail "hdiutil attach failed on the produced .dmg."
mounted_volume="$mount_root"
[[ -d "$mounted_volume/Bundler Mac DMG Fixture.app" ]] || fail "Mounted volume lacks the .app."
[[ -L "$mounted_volume/Applications" ]] || fail "Mounted volume lacks the /Applications drop link."
[[ "$(readlink "$mounted_volume/Applications")" == "/Applications" ]] \
    || fail "The Applications entry is not a symlink to /Applications."
plutil -lint "$mounted_volume/Bundler Mac DMG Fixture.app/Contents/Info.plist" >/dev/null \
    || fail "The .app inside the mounted .dmg has a broken Info.plist."

# 直接启动挂载卷内的 .app（只读卷上的二进制仍可执行；启动标记写不进卷内属预期）。
mounted_app="$mounted_volume/Bundler Mac DMG Fixture.app"
if "$mounted_app/Contents/MacOS/BundlerMacDmgIntegrationFixture" | grep -q "BundlerMacDmgIntegrationFixture"; then
    log "mounted .app launches"
else
    fail "The .app inside the mounted .dmg did not run."
fi

hdiutil detach "$mounted_volume" >/dev/null || fail "hdiutil detach failed."
mounted_volume=""

log "== hdiutil verify =="
hdiutil verify "$dmg_path" >/dev/null || fail "hdiutil verify failed on the produced .dmg."

log "== UDZO compression variant =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-udzo" \
    -p:BundlerTestDmgCompression=Udzo \
    --packages "$package_cache" >/dev/null
udzo_dmg="$integration_root/bundle-udzo/osx-arm64/dmg/Bundler Mac DMG Fixture.dmg"
[[ -f "$udzo_dmg" ]] || fail "The UDZO .dmg variant is missing."
format="$(hdiutil imageinfo "$udzo_dmg" | grep -m1 'Format:' | awk '{print $2}')"
[[ "$format" == "UDZO" ]] || fail "Expected UDZO image format, got '$format'."
mkdir -p "$mount_root"
hdiutil attach "$udzo_dmg" -nobrowse -readonly -mountpoint "$mount_root" >/dev/null \
    || fail "hdiutil attach failed on the UDZO .dmg."
mounted_volume="$mount_root"
[[ -d "$mounted_volume/Bundler Mac DMG Fixture.app" ]] || fail "UDZO volume lacks the .app."
hdiutil detach "$mounted_volume" >/dev/null || fail "UDZO detach failed."
mounted_volume=""

log "PASS: macOS .dmg integration checks passed."
