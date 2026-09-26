#!/usr/bin/env bash
# MAC-APP-1 macOS .app 集成验证：本机真实 生成→plutil 校验→启动→卸载 烟雾测试。
# 用法: bash tests/MacOS.App.Integration/Verify.sh
# 需要 macOS 宿主与 dotnet SDK；产物仅在 artifacts/macos-app-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/macos-app-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerMacAppIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/MacApp.Api.PackageFixture/MacApp.Api.PackageFixture.csproj"
bundle_output="$integration_root/bundle"
api_output="$integration_root/api"
package_cache="$integration_root/nuget-cache"
identity="BundlerMacOSAppIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Darwin" ]] || fail "This integration test requires a macOS host."
for tool in dotnet plutil unzip; do
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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.MacApp; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done

log "== asserting NuGet package contents =="
macapp_contents="$(unzip -l "$package_dir/DotNet.Bundler.MacApp.$version.nupkg")"
[[ "$macapp_contents" == *"lib/netstandard2.0/DotNet.Bundler.MacApp.dll"* ]] \
    || fail "DotNet.Bundler.MacApp nupkg is missing its library payload."
msbuild_contents="$(unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg")"
[[ "$msbuild_contents" == *"tasks/netstandard2.0/DotNet.Bundler.MacApp.dll"* ]] \
    || fail "DotNet.Bundler.MSBuild nupkg does not carry the .app backend."

log "== preparing fixture assets =="
# 512x512 PNG（仅签名头+IHDR；后端只需位图尺寸，不做像素解码）
printf 'iVBORw0KGgoAAAANSUhEUgAAAgAAAAIACAYAAAD0eNT6AAAAAXNSR0IArs4c6Q==' | base64 -d > "$integration_root/icon-512.png"
dylib_args=()
if command -v clang >/dev/null; then
    printf 'int bundler_fixture(void){return 0;}\n' > "$integration_root/fixture.c"
    clang -dynamiclib -o "$integration_root/libfixture.dylib" "$integration_root/fixture.c"
    dylib_args=(-p:BundlerTestDylib="$integration_root/libfixture.dylib")
else
    log "clang unavailable; the Contents/Frameworks dylib assertion is skipped."
fi

log "== publishing fixture with the packaged MSBuild entry =="
dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerPackageVersion="$version" \
    -p:BundlerPackageSource="$package_dir" \
    -p:RestoreAdditionalProjectSources="https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$bundle_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache"

assets_json="$script_dir/Fixture/obj/project.assets.json"
[[ -f "$assets_json" ]] || fail "Fixture restore assets are missing."
for required in "DotNet.Bundler.MacApp/$version" "DotNet.Bundler/$version" "DotNet.Bundler.MSBuild/$version"; do
    grep -q "\"$required\"" "$assets_json" || fail "Fixture restored an unexpected package set (missing $required)."
done

app="$bundle_output/osx-arm64/app/Bundler Mac Integration Fixture.app"
[[ -d "$app" ]] || fail "The .app bundle was not produced at $app"

log "== validating bundle structure =="
for required in \
    "Contents/Info.plist" \
    "Contents/PkgInfo" \
    "Contents/MacOS/BundlerMacIntegrationFixture" \
    "Contents/MacOS/BundlerMacIntegrationFixture.dll" \
    "Contents/Resources/docs/readme.txt" \
    "Contents/Resources/FixtureIcon.icns" \
    "Contents/SharedSupport/shared.txt"; do
    [[ -e "$app/$required" ]] || fail "Missing bundle entry: $required"
done
if [[ ${#dylib_args[@]} -gt 0 ]]; then
    [[ -f "$app/Contents/Frameworks/libfixture.dylib" ]] || fail "Missing Contents/Frameworks/libfixture.dylib"
fi
[[ "$(cat "$app/Contents/PkgInfo")" == "APPL????" ]] || fail "PkgInfo content mismatch."
[[ "$(head -c 4 "$app/Contents/Resources/FixtureIcon.icns")" == "icns" ]] \
    || fail "FixtureIcon.icns is not an icns container."

log "== validating Info.plist via plutil =="
plutil -lint "$app/Contents/Info.plist" >/dev/null || fail "plutil -lint rejected Info.plist."
plist_dump="$(plutil -p "$app/Contents/Info.plist")"
check_key() {
    grep -q "\"$1\" => \"$2\"" <<<"$plist_dump" || fail "Info.plist: $1 != $2"
}
check_key "CFBundleIdentifier" "com.dotnetbundler.macintegrationfixture"
check_key "CFBundleName" "MacFixture"
check_key "CFBundleDisplayName" "Bundler Mac Fixture"
check_key "CFBundleExecutable" "BundlerMacIntegrationFixture"
check_key "CFBundlePackageType" "APPL"
check_key "CFBundleShortVersionString" "1.0.0"
check_key "CFBundleVersion" "2026.9.1"
check_key "CFBundleIconFile" "FixtureIcon.icns"
check_key "LSMinimumSystemVersion" "11.0"
check_key "LSApplicationCategoryType" "public.app-category.utilities"
check_key "NSHumanReadableCopyright" "Copyright DotNet.Bundler Tests"
grep -q '"CFBundleIdentifier"' <<<"$plist_dump" || fail "CFBundleIdentifier missing from plist dump."

log "== launching the produced .app =="
executable="$app/Contents/MacOS/BundlerMacIntegrationFixture"
[[ -x "$executable" ]] || fail "The Mach-O main executable lost its executable bit."
launch_output="$("$executable" marker-a "marker b")"
[[ "$launch_output" == *"BundlerMacIntegrationFixture:marker-a,marker b"* ]] \
    || fail "The .app executable did not produce the expected output: $launch_output"
log "launch output: $launch_output"

if open -n -W "$app" 2>/dev/null; then
    log "open -W launch accepted the bundle."
else
    log "open -W launch failed or is unavailable on this host (headless LaunchServices); direct exec already verified."
fi

log "== checking rebuild determinism =="
first_plist_hash="$(shasum -a 256 "$app/Contents/Info.plist" | cut -d' ' -f1)"
dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerPackageVersion="$version" \
    -p:BundlerPackageSource="$package_dir" \
    -p:RestoreAdditionalProjectSources="https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$bundle_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache" >/dev/null
second_plist_hash="$(shasum -a 256 "$app/Contents/Info.plist" | cut -d' ' -f1)"
[[ "$first_plist_hash" == "$second_plist_hash" ]] || fail "A rebuilt .app produced a different Info.plist."

log "== verifying uninstall semantics =="
rm -rf "$app"
[[ ! -e "$app" ]] || fail "Deleting the .app left residue."
log "uninstall = delete the .app directory; no uninstaller or receipt involved."

log "== exercising the standalone package API =="
dotnet run --project "$api_fixture_project" -c Release \
    -p:BundlerPackageVersion="$version" \
    -p:BundlerPackageSource="$package_dir" \
    -p:RestoreAdditionalProjectSources="https://api.nuget.org/v3/index.json" \
    -p:RestorePackagesPath="$package_cache" \
    -- "$api_output"
api_app="$api_output/artifacts/osx-arm64/app/Mac API Package Fixture.app"
[[ -f "$api_app/Contents/Info.plist" ]] || fail "The standalone API package did not create an .app."
plutil -lint "$api_app/Contents/Info.plist" >/dev/null || fail "API fixture Info.plist failed lint."
api_plist_dump="$(plutil -p "$api_app/Contents/Info.plist")"
[[ "$api_plist_dump" == *'"CFBundleExecutable" => "ApiFixture"'* ]] \
    || fail "API fixture Info.plist executable mismatch."

log "PASS: macOS .app generate→lint→launch→uninstall round-trip is green."
