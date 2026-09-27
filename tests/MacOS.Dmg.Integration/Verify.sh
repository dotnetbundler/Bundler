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
api_fixture_project="$repo_root/tests/MacDmg.Api.PackageFixture/MacDmg.Api.PackageFixture.csproj"
api_output="$integration_root/api"
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

# MAC-DMG-2 branding: background + volume icon are copied unconditionally (no GUI needed).
[[ -f "$mounted_volume/.background/bg.png" ]] \
    || fail "Mounted volume lacks .background/bg.png."
[[ -f "$mounted_volume/.VolumeIcon.icns" ]] \
    || fail "Mounted volume lacks .VolumeIcon.icns."
# Finder 布局需要 GUI 会话；无 GUI 的宿主（CI）降级为警告，镜像仍可挂载。
if [[ -f "$mounted_volume/.DS_Store" ]]; then
    log "Finder layout written (.DS_Store present)"
else
    log "note: headless host — Finder layout degraded (no .DS_Store)"
fi

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

log "== SkipWindowLayout variant =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-skip" \
    -p:BundlerTestDmgSkipWindowLayout=true \
    --packages "$package_cache" >/dev/null
skip_dmg="$integration_root/bundle-skip/osx-arm64/dmg/Bundler Mac DMG Fixture.dmg"
[[ -f "$skip_dmg" ]] || fail "The SkipWindowLayout .dmg variant is missing."
hdiutil verify "$skip_dmg" >/dev/null || fail "hdiutil verify failed on the SkipWindowLayout .dmg."

# MAC-DMG-3: EULA 经 udifrez 注入（udifderez 回读断言资源），DMG 本体 ad-hoc 签名。
log "== EULA + ad-hoc signed variant =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-eula" \
    -p:BundlerTestDmgSkipWindowLayout=true \
    -p:BundlerTestDmgLicense=true \
    -p:BundlerTestDmgSignIdentity=- \
    --packages "$package_cache" >/dev/null
eula_dmg="$integration_root/bundle-eula/osx-arm64/dmg/Bundler Mac DMG Fixture.dmg"
[[ -f "$eula_dmg" ]] || fail "The signed+EULA .dmg variant is missing."
udifderez_xml="$(hdiutil udifderez -xml "$eula_dmg")" \
    || fail "udifderez could not read the SLA resources back."
printf '%s' "$udifderez_xml" | grep -q "<key>LPic</key>" \
    || fail "The embedded SLA is missing the LPic resource."
printf '%s' "$udifderez_xml" | grep -q "<key>STR#</key>" \
    || fail "The embedded SLA is missing the STR# resource."
printf '%s' "$udifderez_xml" | grep -q "<key>TEXT</key>" \
    || fail "The embedded SLA is missing the TEXT license body."
codesign --verify --verbose=2 "$eula_dmg" 2>"$integration_root/codesign-verify.log" \
    || fail "codesign --verify failed on the ad-hoc signed .dmg."
codesign -dvvv "$eula_dmg" 2>&1 | grep -q "Signature=adhoc" \
    || fail "Expected an ad-hoc .dmg signature (Signature=adhoc)."

# SLA 是真实挂载门控：stdin 关闭时 attach 取消，回答 Y 才挂载。
if hdiutil attach "$eula_dmg" -nobrowse -readonly -mountpoint "$mount_root" </dev/null >/dev/null 2>&1; then
    hdiutil detach "$mount_root" >/dev/null 2>&1 || true
    mounted_volume=""
    fail "The SLA image mounted without the license being accepted."
fi
echo Y | hdiutil attach "$eula_dmg" -nobrowse -readonly -mountpoint "$mount_root" >/dev/null \
    || fail "The SLA image did not mount after accepting the license."
mounted_volume="$mount_root"
[[ -d "$mounted_volume/Bundler Mac DMG Fixture.app" ]] \
    || fail "The EULA-mounted volume lacks the .app."
hdiutil detach "$mounted_volume" >/dev/null || fail "EULA-mounted detach failed."
mounted_volume=""

# MAC-DMG-4: quarantine 传播——带 com.apple.quarantine 的 dmg 挂载后，
# 拷出的 .app 携带隔离属性（Gatekeeper 分发语义）。
log "== quarantine propagation =="
quar_dmg="$integration_root/quarantine.dmg"
cp "$dmg_path" "$quar_dmg"
xattr -w com.apple.quarantine \
    "0181;00000000;curl;00000000-0000-0000-0000-000000000000" "$quar_dmg"
mkdir -p "$mount_root"
hdiutil attach "$quar_dmg" -nobrowse -readonly -mountpoint "$mount_root" >/dev/null \
    || fail "hdiutil attach failed on the quarantined .dmg."
mounted_volume="$mount_root"
cp -R "$mounted_volume/Bundler Mac DMG Fixture.app" "$integration_root/quar-app.app"
hdiutil detach "$mounted_volume" >/dev/null || fail "Quarantined detach failed."
mounted_volume=""
xattr -p com.apple.quarantine "$integration_root/quar-app.app" >/dev/null 2>&1 \
    || fail "Quarantine did not propagate to files copied off the .dmg."
rm -rf "$integration_root/quar-app.app" "$quar_dmg"

# osx-x64 产物：结构与挂载断言（本机无 Rosetta，运行态属外部待验收）。
log "== osx-x64 variant =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-x64" \
    -p:RuntimeIdentifier=osx-x64 \
    -p:BundlerTestDmgSkipWindowLayout=true \
    --packages "$package_cache" >/dev/null
x64_dmg="$integration_root/bundle-x64/osx-x64/dmg/Bundler Mac DMG Fixture.dmg"
[[ -f "$x64_dmg" ]] || fail "The osx-x64 .dmg variant is missing."
mkdir -p "$mount_root"
hdiutil attach "$x64_dmg" -nobrowse -readonly -mountpoint "$mount_root" >/dev/null \
    || fail "hdiutil attach failed on the osx-x64 .dmg."
mounted_volume="$mount_root"
file "$mounted_volume/Bundler Mac DMG Fixture.app/Contents/MacOS/BundlerMacDmgIntegrationFixture" \
    | grep -q "x86_64" || fail "The osx-x64 payload is not an x86_64 Mach-O."
hdiutil detach "$mounted_volume" >/dev/null || fail "osx-x64 detach failed."
mounted_volume=""

# 失败路径真实断言：非法压缩值 → publish 失败、无 .dmg 产物、无残留挂载。
log "== failure path leaves nothing behind =="
if dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-bad" \
    -p:BundlerTestDmgCompression=Bogus \
    --packages "$package_cache" >/dev/null 2>&1; then
    fail "An invalid compression value must fail the publish."
fi
if find "$integration_root/bundle-bad" -name '*.dmg' 2>/dev/null | grep -q .; then
    fail "A failed build left a .dmg artifact."
fi
if hdiutil info 2>/dev/null | grep -q "$integration_root"; then
    fail "A failed build left a mounted volume."
fi

log "== exercising the standalone package API =="
dotnet run --project "$api_fixture_project" -c Release \
    -p:BundlerPackageVersion="$version" \
    -p:BundlerPackageSource="$package_dir" \
    -p:RestoreAdditionalProjectSources="https://api.nuget.org/v3/index.json" \
    -p:RestorePackagesPath="$package_cache" \
    -- "$api_output"
api_dmg="$api_output/artifacts/osx-arm64/dmg/DMG API Package Fixture.dmg"
[[ -f "$api_dmg" ]] || fail "The standalone API package did not create a .dmg."
hdiutil attach "$api_dmg" -nobrowse -mountpoint "$integration_root/api-mount" >/dev/null \
    || fail "The API fixture .dmg failed to mount."
[[ -d "$integration_root/api-mount/DMG API Package Fixture.app" && \
   -L "$integration_root/api-mount/Applications" ]] \
    || fail "The API fixture .dmg volume lacks the .app or the /Applications link."
hdiutil detach "$integration_root/api-mount" >/dev/null || fail "Failed to detach the API fixture volume."

log "PASS: macOS .dmg integration checks passed."
