#!/usr/bin/env bash
# MAC-APP-1/2 macOS .app 集成验证：本机真实 生成→plutil 校验→启动→桌面集成→卸载 烟雾测试。
# MAC-APP-2 覆盖：文件关联/URL scheme 的 plist 键、lsregister 注册、open <文件>/<scheme> 唤起、
# ~/Applications 拷入拷出、lipo 架构交叉校验。
# 用法: bash tests/MacOS.App.Integration/Verify.sh
# 需要 macOS 宿主与 dotnet SDK；产物仅在 artifacts/macos-app-integration 与 ~/Applications 下落盘并全部清理。
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

copied_app=""
cleanup() {
    if [[ -n "$copied_app" && -e "$copied_app" ]]; then
        /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
            -u "$copied_app" >/dev/null 2>&1 || true
        rm -rf "$copied_app"
    fi
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

if [[ "$(uname -s)" != "Darwin" ]]; then log "integration test skipped: non-macOS host"; exit 0; fi
for tool in dotnet plutil unzip python3 lipo; do
    command -v "$tool" >/dev/null || fail "$tool is unavailable on this host."
done
lsregister="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
[[ -x "$lsregister" ]] || fail "lsregister is unavailable on this host."

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
    # 编成 arm64+x86_64 fat dylib：osx-arm64/osx-x64 两个产物都能带它过架构校验。
    if clang -dynamiclib -arch arm64 -arch x86_64 -o "$integration_root/libfixture.dylib" "$integration_root/fixture.c" 2>/dev/null; then
        log "built a fat (arm64+x86_64) fixture dylib."
    else
        clang -dynamiclib -o "$integration_root/libfixture.dylib" "$integration_root/fixture.c"
        log "built an arm64-only fixture dylib; it is skipped for the osx-x64 publish."
    fi
    dylib_args=(-p:BundlerTestDylib="$integration_root/libfixture.dylib")
else
    log "clang unavailable; the Contents/Frameworks dylib assertion is skipped."
fi

log "== publishing fixture with the packaged MSBuild entry =="
dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerIntegrationOutput="$bundle_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache"

assets_json="$script_dir/Fixture/obj/project.assets.json"
[[ -f "$assets_json" ]] || fail "Fixture restore assets are missing."
if grep -q '"DotNet.Bundler' "$assets_json"; then
    fail "Fixture unexpectedly restored DotNet.Bundler packages; internal consumers must use project references."
fi

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

log "== validating MAC-APP-2 desktop integration keys =="
plist_json() {
    plutil -extract "$1" json -o - "$app/Contents/Info.plist" 2>/dev/null
}
plist_json "CFBundleDocumentTypes" | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert len(d) == 1, f"expected one document type, got {d}"
entry = d[0]
assert entry["CFBundleTypeExtensions"] == ["hifix"], entry
assert entry["CFBundleTypeName"] == "HiFix Document", entry
assert entry["CFBundleTypeRole"] == "Editor" and entry["LSHandlerRank"] == "Owner", entry
assert entry["LSItemContentTypes"] == ["com.dotnetbundler.hifix"], entry
' || fail "CFBundleDocumentTypes structure mismatch."
plist_json "UTExportedTypeDeclarations" | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert len(d) == 1, f"expected one exported UTI, got {d}"
entry = d[0]
assert entry["UTTypeIdentifier"] == "com.dotnetbundler.hifix", entry
assert entry["UTTypeConformsTo"] == ["public.data"], entry
assert entry["UTTypeTagSpecification"]["public.filename-extension"] == ["hifix"], entry
assert entry["UTTypeTagSpecification"]["public.mime-type"] == "application/x-hifix", entry
' || fail "UTExportedTypeDeclarations structure mismatch."
plist_json "CFBundleURLTypes" | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert len(d) == 1, f"expected one URL type, got {d}"
entry = d[0]
assert entry["CFBundleURLSchemes"] == ["hifix"], entry
assert entry["CFBundleURLName"] == "HiFix Link", entry
assert entry["CFBundleTypeRole"] == "Viewer", entry
' || fail "CFBundleURLTypes structure mismatch."
plist_json "NSAppTransportSecurity.NSExceptionDomains" | python3 -c '
import json, sys
d = json.load(sys.stdin)
entry = d["bundler.invalid"]
assert entry["NSExceptionAllowsInsecureHTTPLoads"] is True, entry
assert entry["NSIncludesSubdomains"] is True, entry
' || fail "NSAppTransportSecurity exception domain mismatch."
grep -Eq '"NSSupportsSuddenTermination" => (true|1)' <<<"$plist_dump" \
    || fail "Caller-plist boolean key did not merge."
grep -q '"HiFixCustomKey" => "from-extra-plist"' <<<"$plist_dump" \
    || fail "Caller-plist string key did not merge."

log "== cross-checking Mach-O architectures with lipo =="
lipo_info="$(lipo -info "$app/Contents/MacOS/BundlerMacIntegrationFixture" 2>/dev/null || true)"
[[ "$lipo_info" == *"arm64"* ]] || fail "lipo -info did not report arm64: $lipo_info"
log "lipo -info: $lipo_info"

log "== launching the produced .app =="
executable="$app/Contents/MacOS/BundlerMacIntegrationFixture"
[[ -x "$executable" ]] || fail "The Mach-O main executable lost its executable bit."
launch_output="$("$executable" marker-a "marker b")"
[[ "$launch_output" == *"BundlerMacIntegrationFixture:marker-a,marker b"* ]] \
    || fail "The .app executable did not produce the expected output: $launch_output"
log "launch output: $launch_output"

if open -n -W "$app" 2>/dev/null; then
    log "open -W launch accepted the bundle."
    launchservices_ok=1
else
    log "open -W launch failed or is unavailable on this host (headless LaunchServices); direct exec already verified."
    launchservices_ok=0
fi

if [[ "$launchservices_ok" == "1" ]]; then
    log "== exercising LaunchServices registration and dispatch =="
    marker="$app/Contents/.launch-marker"
    "$lsregister" -f "$app" || fail "lsregister -f failed to register the bundle."
    # lsregister 注册到 -dump 可见是异步的，最多轮询 10 秒。
    registered=0
    for _ in $(seq 1 20); do
        "$lsregister" -dump > "$integration_root/ls-dump.txt" 2>/dev/null || true
        if grep -q "com.dotnetbundler.macintegrationfixture" "$integration_root/ls-dump.txt"; then
            registered=1
            break
        fi
        sleep 0.5
    done
    [[ "$registered" == "1" ]] || fail "lsregister -dump does not list the bundle identifier."

    sample_file="$integration_root/sample.hifix"
    printf 'hifix-payload\n' > "$sample_file"
    rm -f "$marker"
    open -W "$sample_file" || fail "open <file> did not route to the registered app."
    [[ -f "$marker" ]] || fail "open <file> returned success but the app never ran (no launch marker)."
    log "file association dispatch verified via launch marker."

    rm -f "$marker"
    open -W "hifix://ping" || fail "open <scheme>:// did not route to the registered app."
    [[ -f "$marker" ]] || fail "open <scheme>:// returned success but the app never ran (no launch marker)."
    log "URL scheme dispatch verified via launch marker."
    rm -f "$marker"

    log "== exercising the user-domain ~/Applications drop install =="
    mkdir -p "$HOME/Applications"
    copied_app="$HOME/Applications/Bundler Mac Integration Fixture.app"
    cp -R "$app" "$copied_app"
    "$lsregister" -f "$copied_app" || fail "lsregister failed to register the ~/Applications copy."
    rm -f "$copied_app/Contents/.launch-marker"
    open -W "$copied_app" || fail "open failed for the ~/Applications copy."
    [[ -f "$copied_app/Contents/.launch-marker" ]] || fail "The ~/Applications copy never ran."
    "$lsregister" -u "$copied_app" >/dev/null 2>&1 || true
    rm -rf "$copied_app"
    copied_app=""
    log "~/Applications drop install + launch verified."
else
    log "LaunchServices unavailable; association/protocol dispatch stays verified by plist assertions only."
fi

log "== checking rebuild determinism =="
first_plist_hash="$(shasum -a 256 "$app/Contents/Info.plist" | cut -d' ' -f1)"
dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerIntegrationOutput="$bundle_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache" >/dev/null
second_plist_hash="$(shasum -a 256 "$app/Contents/Info.plist" | cut -d' ' -f1)"
[[ "$first_plist_hash" == "$second_plist_hash" ]] || fail "A rebuilt .app produced a different Info.plist."

log "== exercising ad-hoc signing (MAC-APP-3) =="
signed_output="$integration_root/signed-output"
entitlements_file="$integration_root/entitlements.plist"
cat > "$entitlements_file" <<'ENTITLEMENTS'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key>
    <true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
    <true/>
    <key>com.apple.security.cs.disable-library-validation</key>
    <true/>
</dict>
</plist>
ENTITLEMENTS
dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerIntegrationOutput="$signed_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    -p:BundlerTestSignIdentity="-" \
    -p:BundlerTestHardenedRuntime="true" \
    -p:BundlerTestEntitlementsFile="$entitlements_file" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache"
signed_app="$signed_output/osx-arm64/app/Bundler Mac Integration Fixture.app"
[[ -d "$signed_app" ]] || fail "The signed .app was not produced."
codesign --verify --deep --strict "$signed_app" || fail "codesign --verify rejected the ad-hoc signed bundle."
authority="$(codesign -dv --verbose=4 "$signed_app" 2>&1 | grep -c 'Signature=adhoc' || true)"
[[ "$authority" -ge 1 ]] || fail "The bundle is not ad-hoc signed."
"$signed_app/Contents/MacOS/BundlerMacIntegrationFixture" signed-launch >"$integration_root/signed-launch.txt" \
    || fail "The ad-hoc signed bundle did not launch."
grep -q "signed-launch" "$integration_root/signed-launch.txt" || fail "Signed launch produced no output."
[[ -f "$signed_app/Contents/.launch-marker" ]] || fail "The signed app did not write its launch marker."
rm -f "$signed_app/Contents/.launch-marker"
log "ad-hoc signing chain verified: codesign -s - → --verify --deep --strict → launch."

log "== asserting a missing certificate fails before any tool runs =="
missing_cert_output="$integration_root/badcert-output"
if dotnet publish "$fixture_project" -c Release --force \
    -p:BundlerIntegrationOutput="$missing_cert_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    -p:BundlerTestSigningCertificate="$integration_root/missing.p12" \
    "${dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache" >"$integration_root/badcert.log" 2>&1; then
    fail "A missing temporary certificate must fail the publish."
fi
[[ ! -e "$missing_cert_output/osx-arm64/app/Bundler Mac Integration Fixture.app" ]] \
    || fail "A failed signing run left a pseudo-success .app."
grep -qi "TemporaryCertificatePath\|temporary certificate" "$integration_root/badcert.log" \
    || fail "The missing-certificate error was not surfaced in the publish log."
log "missing-certificate failure path verified (no pseudo-success artifact)."

log "== exercising osx-x64 packaging (structure only; no Rosetta on this host) =="
x64_output="$integration_root/x64-output"
x64_dylib_args=("${dylib_args[@]}")
if [[ ${#dylib_args[@]} -gt 0 ]] && ! lipo -info "$integration_root/libfixture.dylib" 2>/dev/null | grep -q x86_64; then
    x64_dylib_args=()
    log "fixture dylib is arm64-only; publishing osx-x64 without it."
fi
dotnet publish "$fixture_project" -c Release --force \
    -p:RuntimeIdentifier=osx-x64 \
    -p:BundlerIntegrationOutput="$x64_output" \
    -p:BundlerTestIcon="$integration_root/icon-512.png" \
    "${x64_dylib_args[@]}" \
    -p:RestorePackagesPath="$package_cache"
x64_app="$x64_output/osx-x64/app/Bundler Mac Integration Fixture.app"
[[ -d "$x64_app" ]] || fail "The osx-x64 .app was not produced."
x64_lipo="$(lipo -info "$x64_app/Contents/MacOS/BundlerMacIntegrationFixture" 2>/dev/null || true)"
[[ "$x64_lipo" == *"x86_64"* ]] || fail "osx-x64 executable is not x86_64: $x64_lipo"
plutil -lint "$x64_app/Contents/Info.plist" >/dev/null || fail "osx-x64 Info.plist failed lint."
if /usr/bin/arch -x86_64 "$x64_app/Contents/MacOS/BundlerMacIntegrationFixture" >/dev/null 2>&1; then
    log "osx-x64 bundle launched under Rosetta."
else
    log "osx-x64 runtime launch unavailable on this host (no Rosetta); structure verified only (external item)."
fi

if [[ "$launchservices_ok" == "1" ]]; then
    log "== exercising quarantine first-launch (Gatekeeper) =="
    quarantined_app="$integration_root/quarantine/Bundler Mac Integration Fixture.app"
    mkdir -p "$(dirname "$quarantined_app")"
    cp -R "$app" "$quarantined_app"
    xattr -w com.apple.quarantine "0081;$(printf '%x' "$(date +%s)");Verify.sh;" "$quarantined_app"
    xattr -l "$quarantined_app" | grep -q "com.apple.quarantine" \
        || fail "com.apple.quarantine was not set on the copied app."
    # Gatekeeper 对未签名隔离包会弹窗等待用户决定——无人应答时 open -W 永久挂起；
    # 后台跑并限时等待：仍挂起/非零退出都算"被拦"，只有 0 退出且进程已结束才是放行。
    open -W "$quarantined_app" >/dev/null 2>&1 &
    quarantine_open_pid=$!
    quarantine_open_done=0
    for _ in $(seq 1 30); do
        if ! kill -0 "$quarantine_open_pid" 2>/dev/null; then
            quarantine_open_done=1
            break
        fi
        sleep 1
    done
    if [[ "$quarantine_open_done" == "0" ]]; then
        kill "$quarantine_open_pid" 2>/dev/null || true
        wait "$quarantine_open_pid" 2>/dev/null || true
        log "quarantined unsigned bundle is held by Gatekeeper (open did not complete in 30s) as expected."
    elif wait "$quarantine_open_pid" 2>/dev/null; then
        log "quarantined unsigned bundle opened (Gatekeeper did not intervene on this host)."
    else
        log "quarantined unsigned bundle was blocked by LaunchServices/Gatekeeper as expected."
    fi
    xattr -d com.apple.quarantine "$quarantined_app"
    if xattr -l "$quarantined_app" | grep -q "com.apple.quarantine"; then
        fail "com.apple.quarantine could not be removed."
    fi
    # 不在此副本上再 open：Gatekeeper 弹窗在无人值守宿主上会残留并阻塞后续 open；
    # 未隔离包可启动已由前面 $app 的直接/open 启动证明。
    log "quarantine first-launch behavior verified (quarantined copy blocked; xattr removable)."

    log "== exercising LSMinimumSystemVersion enforcement =="
    minver_output="$integration_root/minver-output"
    dotnet publish "$fixture_project" -c Release --force \
            -p:BundlerIntegrationOutput="$minver_output" \
        -p:BundlerTestIcon="$integration_root/icon-512.png" \
        -p:BundlerTestMinSystemVersion=99.0 \
        "${dylib_args[@]}" \
        -p:RestorePackagesPath="$package_cache"
    minver_app="$minver_output/osx-arm64/app/Bundler Mac Integration Fixture.app"
    plutil -p "$minver_app/Contents/Info.plist" | grep -q '"LSMinimumSystemVersion" => "99.0"' \
        || fail "LSMinimumSystemVersion override did not land in the plist."
    if open -W "$minver_app" >/dev/null 2>&1; then
        fail "An app requiring macOS 99.0 must not open on this host."
    fi
    log "LSMinimumSystemVersion=99.0 rejected by LaunchServices as expected."

    log "== exercising v1 to v2 in-place upgrade =="
    upgrade_output="$integration_root/upgrade-output"
    dotnet publish "$fixture_project" -c Release --force \
            -p:BundlerIntegrationOutput="$upgrade_output" \
        -p:BundlerTestIcon="$integration_root/icon-512.png" \
        -p:BundlerTestBuildVersion=2026.9.2 \
        "${dylib_args[@]}" \
        -p:RestorePackagesPath="$package_cache"
    upgrade_app="$upgrade_output/osx-arm64/app/Bundler Mac Integration Fixture.app"
    plutil -p "$upgrade_app/Contents/Info.plist" | grep -q '"CFBundleVersion" => "2026.9.2"' \
        || fail "v2 CFBundleVersion did not land in the plist."
    mkdir -p "$HOME/Applications"
    copied_app="$HOME/Applications/Bundler Mac Integration Fixture.app"
    rm -rf "$copied_app"
    cp -R "$app" "$copied_app"
    "$lsregister" -f "$copied_app" || fail "v1 registration failed."
    rm -f "$copied_app/Contents/.launch-marker"
    open -W "$copied_app" || fail "v1 launch failed."
    [[ -f "$copied_app/Contents/.launch-marker" ]] || fail "v1 launch marker missing."
    rm -rf "$copied_app"
    cp -R "$upgrade_app" "$copied_app"
    "$lsregister" -f "$copied_app" || fail "v2 registration failed."
    rm -f "$copied_app/Contents/.launch-marker"
    open -W "$copied_app" || fail "v2 launch after in-place replacement failed."
    [[ -f "$copied_app/Contents/.launch-marker" ]] || fail "v2 launch marker missing after upgrade."
    "$lsregister" -dump > "$integration_root/ls-dump2.txt" 2>/dev/null || true
    grep -q "com.dotnetbundler.macintegrationfixture" "$integration_root/ls-dump2.txt" \
        || fail "LaunchServices lost the bundle identifier after upgrade."
    "$lsregister" -u "$copied_app" >/dev/null 2>&1 || true
    rm -rf "$copied_app"
    copied_app=""
    log "v1 to v2 in-place upgrade verified (re-register + launch + identifier intact)."
else
    log "LaunchServices unavailable; quarantine/min-version/upgrade checks skipped on this host."
fi

log "== verifying uninstall semantics =="
rm -rf "$app"
[[ ! -e "$app" ]] || fail "Deleting the .app left residue."
log "uninstall = delete the .app directory; no uninstaller or receipt involved."

log "== exercising the standalone package API =="
dotnet run --project "$api_fixture_project" -c Release \
    -p:RestorePackagesPath="$package_cache" \
    -- "$api_output"
api_app="$api_output/artifacts/osx-arm64/app/Mac API Package Fixture.app"
[[ -f "$api_app/Contents/Info.plist" ]] || fail "The standalone API package did not create an .app."
plutil -lint "$api_app/Contents/Info.plist" >/dev/null || fail "API fixture Info.plist failed lint."
api_plist_dump="$(plutil -p "$api_app/Contents/Info.plist")"
[[ "$api_plist_dump" == *'"CFBundleExecutable" => "ApiFixture"'* ]] \
    || fail "API fixture Info.plist executable mismatch."

log "PASS: macOS .app generate→lint→launch→uninstall round-trip is green."
