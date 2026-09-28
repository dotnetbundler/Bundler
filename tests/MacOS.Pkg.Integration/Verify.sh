#!/usr/bin/env bash
# MAC-PKG-1 macOS .pkg 集成验证：真实 pkgbuild 链路 生成→expand-full→payload/PackageInfo 断言→xar/dominfo。
# 用法: bash tests/MacOS.Pkg.Integration/Verify.sh
# 需要 macOS 宿主与 dotnet SDK；产物仅在 artifacts/macos-pkg-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/macos-pkg-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerMacPkgIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/MacPkg.Api.PackageFixture/MacPkg.Api.PackageFixture.csproj"
api_output="$integration_root/api"
package_cache="$integration_root/nuget-cache"
expand_root="$integration_root/expand"
identity="BundlerMacOSPkgIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Darwin" ]] || fail "This integration test requires a macOS host."
for tool in dotnet pkgutil xar installer plutil; do
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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.MacApp DotNet.Bundler.MacPkg; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 pkg 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.MacPkg.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the MacPkg backend assembly."

log "== publishing fixture (BundlerFormats=pkg) =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle" \
    --packages "$package_cache" >/dev/null

app_bundle="$integration_root/bundle/osx-arm64/app/Bundler Mac PKG Fixture.app"
pkg_path="$integration_root/bundle/osx-arm64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -d "$app_bundle" ]] || fail "The planner did not produce the intermediate .app: $app_bundle"
[[ -f "$pkg_path" ]] || fail "The .pkg artifact is missing: $pkg_path"
plutil -lint "$app_bundle/Contents/Info.plist" >/dev/null || fail "Intermediate .app Info.plist is invalid."
log "produced: $pkg_path"

log "== expanding the .pkg and asserting payload contents =="
mkdir -p "$expand_root"
pkgutil --expand-full "$pkg_path" "$expand_root/root" >/dev/null \
    || fail "pkgutil --expand-full failed on the produced .pkg."
# expand-full 目录结构：<root>/PackageInfo + <root>/Payload（已解包成文件树）。
payload_dir="$expand_root/root/Payload"
[[ -d "$expand_root/root" ]] || fail "The expanded package root is missing."
[[ -f "$expand_root/root/PackageInfo" ]] || fail "The expanded package lacks PackageInfo."
[[ -d "$payload_dir/Bundler Mac PKG Fixture.app" ]] \
    || fail "The payload lacks the .app at its root (expand-full tree: $(ls "$expand_root/root"))."
[[ -f "$payload_dir/support/helper.txt" ]] \
    || fail "The payload lacks the explicit BundlerPkgPayload item at support/helper.txt."
plutil -lint "$payload_dir/Bundler Mac PKG Fixture.app/Contents/Info.plist" >/dev/null \
    || fail "The .app inside the payload has a broken Info.plist."
# payload 内二进制真实启动（pkg 解包出的副本可执行）。
# pipefail 下 `<cmd> | grep -q` 有 SIGPIPE 竞态（grep 命中即退会打断上游写），
# 先捕获输出再 grep，避免间歇性误判。
payload_output="$("$payload_dir/Bundler Mac PKG Fixture.app/Contents/MacOS/BundlerMacPkgIntegrationFixture" 2>&1)" \
    || fail "The .app inside the expanded payload did not run."
printf '%s' "$payload_output" | grep -q "BundlerMacPkgIntegrationFixture" \
    || fail "The .app inside the expanded payload did not run."
log "payload .app launches"

log "== PackageInfo metadata assertions =="
package_info="$(cat "$expand_root/root/PackageInfo")"
printf '%s' "$package_info" | grep -q 'identifier="com.dotnetbundler.macpkgintegrationfixture"' \
    || fail "PackageInfo lacks the default identifier (bundle Identifier)."
printf '%s' "$package_info" | grep -q 'version="1.0.0"' \
    || fail "PackageInfo lacks the bundle version."
printf '%s' "$package_info" | grep -q 'install-location="/Applications"' \
    || fail "PackageInfo lacks the default install location /Applications."

log "== xar structure =="
xar_listing="$(xar -tf "$pkg_path")" || fail "xar -tf failed on the produced .pkg."
printf '%s' "$xar_listing" | grep -q "PackageInfo" || fail "xar listing lacks PackageInfo."
printf '%s' "$xar_listing" | grep -q "Payload" || fail "xar listing lacks Payload."
printf '%s' "$xar_listing" | grep -q "Bom" || fail "xar listing lacks Bom."

log "== installer -dominfo / -pkginfo =="
# 组件包不含 domains 声明（域名声明属分发包的 distribution.xml，MAC-PKG-2）；
# 这里断言 installer 能解析该包：-dominfo -plist 须返回合法 plist（组件包返回空数组属预期）。
dominfo="$(installer -dominfo -pkg "$pkg_path" -plist)" \
    || fail "installer -dominfo could not parse the package."
printf '%s' "$dominfo" | grep -q "<array/>" \
    || fail "installer -dominfo returned unexpected output: '$dominfo'."

# -pkginfo 回读组件包元信息（identifier/version 在 PackageInfo 断言过，这里验证 installer 视角）。
pkginfo="$(installer -pkginfo -pkg "$pkg_path" 2>&1 || true)"
printf '%s' "$pkginfo" | grep -qi "Bundler Mac PKG Fixture" \
    || fail "installer -pkginfo could not parse the package: '$pkginfo'."

log "== override variant (identifier/version/install-location) =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-override" \
    -p:BundlerTestPkgIdentifier=com.example.custom.pkg \
    -p:BundlerTestPkgVersion=9.9.9 \
    -p:BundlerTestPkgInstallLocation=/opt/bundler-test \
    --packages "$package_cache" >/dev/null
override_pkg="$integration_root/bundle-override/osx-arm64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -f "$override_pkg" ]] || fail "The override .pkg variant is missing."
pkgutil --expand-full "$override_pkg" "$expand_root/override" >/dev/null \
    || fail "pkgutil --expand-full failed on the override .pkg."
override_info="$(cat "$expand_root/override/PackageInfo")"
printf '%s' "$override_info" | grep -q 'identifier="com.example.custom.pkg"' \
    || fail "Override identifier missing from PackageInfo."
printf '%s' "$override_info" | grep -q 'version="9.9.9"' \
    || fail "Override version missing from PackageInfo."
printf '%s' "$override_info" | grep -q 'install-location="/opt/bundler-test"' \
    || fail "Override install location missing from PackageInfo."

log "== distribution package variant (pages + per-user domain) =="
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-dist" \
    -p:BundlerTestPkgTitle="Fixture Installer" \
    -p:BundlerTestPkgWelcome=true \
    -p:BundlerTestPkgConclusion=true \
    -p:BundlerTestPkgLicense=true \
    -p:BundlerTestPkgDomain=CurrentUserHome \
    --packages "$package_cache" >/dev/null
dist_pkg="$integration_root/bundle-dist/osx-arm64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -f "$dist_pkg" ]] || fail "The distribution .pkg variant is missing."
xar_listing="$(xar -tf "$dist_pkg")" || fail "xar -tf failed on the distribution .pkg."
printf '%s' "$xar_listing" | grep -q "Distribution" \
    || fail "A distribution .pkg must contain a Distribution document."
printf '%s' "$xar_listing" | grep -q "component.pkg" \
    || fail "A distribution .pkg must embed the component package."
printf '%s' "$xar_listing" | grep -q "Resources/welcome.txt" \
    || fail "The distribution .pkg is missing the welcome resource."
printf '%s' "$xar_listing" | grep -q "Resources/conclusion.rtf" \
    || fail "The distribution .pkg is missing the conclusion resource."
printf '%s' "$xar_listing" | grep -q "Resources/license.txt" \
    || fail "The distribution .pkg is missing the license resource."
mkdir -p "$expand_root/dist-doc"
xar -xf "$dist_pkg" -C "$expand_root/dist-doc" \
    || fail "Could not extract the Distribution document."
[[ -f "$expand_root/dist-doc/Distribution" ]] \
    || fail "The extracted package lacks the Distribution document."
distribution_doc="$(cat "$expand_root/dist-doc/Distribution")"
printf '%s' "$distribution_doc" | grep -q "<title>Fixture Installer</title>" \
    || fail "The Distribution document lacks the configured title."
printf '%s' "$distribution_doc" | grep -q 'enable_currentUserHome="true"' \
    || fail "The Distribution document lacks the current-user-home domain."
dominfo="$(installer -dominfo -pkg "$dist_pkg" -plist)" \
    || fail "installer -dominfo failed on the distribution .pkg."
printf '%s' "$dominfo" | grep -qi "currentuserhome" \
    || fail "installer -dominfo did not report the CurrentUserHome domain: '$dominfo'."

log "== scripts knob: postinstall archived into the package =="
rm -f "$HOME/.bundler-pkg-postinstall-ran"
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-scripts" \
    -p:BundlerTestPkgScripts=true \
    -p:BundlerTestPkgDomain=CurrentUserHome \
    --packages "$package_cache" >/dev/null
scripts_pkg="$integration_root/bundle-scripts/osx-arm64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -f "$scripts_pkg" ]] || fail "The scripts variant .pkg is missing."
# 脚本归档进内嵌组件包：展开 product 包再找 component.pkg 内的 Scripts 载荷。
mkdir -p "$expand_root/scripts-product"
xar -xf "$scripts_pkg" -C "$expand_root/scripts-product" >/dev/null \
    || fail "xar could not unpack the scripts variant .pkg."
# 内嵌组件包在 xar 中是展开形态（component.pkg/Bom|Payload|PackageInfo|Scripts）；
# Scripts 是 cpio 归档，解开它断言 postinstall 确实被收进包里。
component_scripts="$expand_root/scripts-product/component.pkg/Scripts"
[[ -f "$component_scripts" ]] \
    || fail "pkgbuild did not archive a Scripts payload into the component package."
mkdir -p "$expand_root/scripts-cpio"
( cd "$expand_root/scripts-cpio" && cat "$component_scripts" | gunzip | cpio -i --quiet ) \
    || fail "The Scripts cpio archive could not be unpacked."
[[ -f "$expand_root/scripts-cpio/postinstall" ]] \
    || fail "The Scripts archive does not contain the postinstall script."

log "== signing plumbing: bogus identity fails honestly =="
# 没有真实 Developer ID Installer 证书（外部待验收 MAC-PKG-OI-02），
# 但签名接线可被实测：不存在的身份必须让 pkgbuild --sign 诚实失败。
if dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-sign-fail" \
    -p:BundlerTestPkgSignIdentity="Nonexistent Installer Identity" \
    --packages "$package_cache" >/dev/null 2>&1; then
    fail "A nonexistent signing identity must fail the publish."
fi
if [[ -n "$(find "$integration_root/bundle-sign-fail" -name '*.pkg' -print -quit 2>/dev/null)" ]]; then
    fail "A failed signing build left a .pkg artifact."
fi

# 本机唯一可无管理员实测的安装路径：current-user-home 域真实安装 + 收据断言。
# per-user 收据写 ~/Library/Receipts，pkgutil 需要 --volume ~ 才能看到。
# 注意：installer 对 <relocate> 标记的 bundle 有重定位行为——若同 id 的 .app 已存在于
# 本机别处（本脚本此前产出的各变体中间 .app），payload 会被装到那个旧位置。
# 安装前清掉 artifacts 树下全部中间 .app，确保按 install-location 落到 ~/Applications。
find "$integration_root" -name "*.app" -type d -exec rm -rf {} + 2>/dev/null || true
log "== real per-user install (CurrentUserHome domain, scripts variant) =="
# 用 scripts 变体实测安装：postinstall 由 installer 真实执行并留下标记文件。
home_app="$HOME/Applications/Bundler Mac PKG Fixture.app"
home_helper="$HOME/Applications/support/helper.txt"
if ! installer -pkg "$scripts_pkg" -target CurrentUserHomeDirectory -dumplog \
        >"$integration_root/install.log" 2>&1; then
    cat "$integration_root/install.log" >&2 || true
    fail "installer could not install the distribution pkg into the home domain."
fi
[[ -d "$home_app" ]] || fail "The per-user install did not place the .app under ~/Applications."
[[ -f "$home_helper" ]] || fail "The per-user install did not place the payload helper under ~/Applications/support."
plutil -lint "$home_app/Contents/Info.plist" >/dev/null \
    || fail "The installed .app has a broken Info.plist."
home_output="$("$home_app/Contents/MacOS/BundlerMacPkgIntegrationFixture" 2>&1)" \
    || fail "The installed .app did not run."
printf '%s' "$home_output" | grep -q "BundlerMacPkgIntegrationFixture" \
    || fail "The installed .app did not run."
pkgs_list="$(pkgutil --pkgs --volume ~)" \
    || fail "pkgutil --pkgs --volume ~ failed."
printf '%s' "$pkgs_list" | grep -q "com.dotnetbundler.macpkgintegrationfixture" \
    || fail "pkgutil --pkgs --volume ~ lacks the fixture receipt."
pkg_files="$(pkgutil --files com.dotnetbundler.macpkgintegrationfixture --volume ~)" \
    || fail "pkgutil --files failed for the fixture receipt."
printf '%s' "$pkg_files" | grep -q "support/helper.txt" \
    || fail "pkgutil --files lacks the payload entry for the fixture."
pkgutil --forget com.dotnetbundler.macpkgintegrationfixture --volume ~ >/dev/null \
    || fail "pkgutil --forget failed for the fixture receipt."
[[ -f "$HOME/.bundler-pkg-postinstall-ran" ]] \
    || fail "The postinstall script was not executed during the real install."
rm -f "$HOME/.bundler-pkg-postinstall-ran"

log "== overwrite-install upgrade (same identifier, version bump) =="
# 覆盖安装升级语义：同 identifier 的 v2 装上后，收据版本必须更新到 2.0.0。
dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-v2" \
    -p:BundlerTestPkgVersion=2.0.0 \
    -p:BundlerTestPkgDomain=CurrentUserHome \
    --packages "$package_cache" >/dev/null
v2_pkg="$integration_root/bundle-v2/osx-arm64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -f "$v2_pkg" ]] || fail "The v2 .pkg variant is missing."
if ! installer -pkg "$v2_pkg" -target CurrentUserHomeDirectory -dumplog \
        >"$integration_root/install-v2.log" 2>&1; then
    cat "$integration_root/install-v2.log" >&2 || true
    fail "The v2 overwrite install failed."
fi
pkg_info="$(pkgutil --pkg-info-plist com.dotnetbundler.macpkgintegrationfixture --volume ~)" \
    || fail "pkgutil --pkg-info-plist failed after the upgrade install."
printf '%s' "$pkg_info" | grep -q "<string>2.0.0</string>" \
    || fail "The receipt version did not move to 2.0.0 after overwrite install: $pkg_info"
pkgutil --forget com.dotnetbundler.macpkgintegrationfixture --volume ~ >/dev/null || true
rm -rf "$home_app" "$HOME/Applications/support"

log "== osx-x64 artifact variant =="
dotnet publish "$fixture_project" -c Release -r osx-x64 \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-x64" \
    --packages "$package_cache" >/dev/null
x64_pkg="$integration_root/bundle-x64/osx-x64/pkg/Bundler Mac PKG Fixture.pkg"
[[ -f "$x64_pkg" ]] || fail "The osx-x64 .pkg variant is missing."
pkgutil --expand-full "$x64_pkg" "$expand_root/x64" >/dev/null \
    || fail "pkgutil --expand-full failed on the osx-x64 .pkg."
x64_bin="$expand_root/x64/Payload/Bundler Mac PKG Fixture.app/Contents/MacOS/BundlerMacPkgIntegrationFixture"
[[ -f "$x64_bin" ]] || fail "The osx-x64 payload lacks the main executable."
file "$x64_bin" | grep -q "x86_64" \
    || fail "The osx-x64 payload binary is not x86_64: $(file "$x64_bin")"
# 运行态需 Rosetta——本机不具备，登记外部待验收（MAC-PKG-OI-04）。

log "== failure path leaves nothing behind =="
if dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/bundle-bad" \
    -p:BundlerTestPkgInstallLocation=relative/path \
    --packages "$package_cache" >/dev/null 2>&1; then
    fail "A relative install location must fail the publish."
fi
if [[ -n "$(find "$integration_root/bundle-bad" -name '*.pkg' -print -quit 2>/dev/null)" ]]; then
    fail "A failed build left a .pkg artifact."
fi

log "== exercising the standalone package API =="
dotnet run --project "$api_fixture_project" -c Release \
    -p:BundlerPackageVersion="$version" \
    -p:BundlerPackageSource="$package_dir" \
    -p:RestoreAdditionalProjectSources="https://api.nuget.org/v3/index.json" \
    -p:RestorePackagesPath="$package_cache" \
    -- "$api_output"
api_pkg="$api_output/artifacts/osx-arm64/pkg/PKG API Package Fixture.pkg"
[[ -f "$api_pkg" ]] || fail "The standalone API package did not create a .pkg."
pkgutil --expand-full "$api_pkg" "$expand_root/api" >/dev/null \
    || fail "pkgutil --expand-full failed on the API fixture .pkg."
[[ -d "$expand_root/api/Payload/PKG API Package Fixture.app" ]] \
    || fail "The API fixture .pkg payload lacks the .app bundle."

log "PASS: macOS .pkg integration checks passed."
