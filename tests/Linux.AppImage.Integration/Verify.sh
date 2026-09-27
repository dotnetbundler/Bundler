#!/usr/bin/env bash
# LINUX-APPIMAGE-1 .AppImage 集成验证：真实 .NET payload → BundlerFormats=appimage
# → 命名/侧车断言 → --appimage-extract 结构断言（AppRun/根 desktop 链接/.DirIcon/usr 树）
# → 解出 AppRun 与整包 --appimage-extract-and-run 真实运行 → 覆盖变体
# → arm64 结构断言（ELF e_machine，宿主不可执行故不解包）→ deb;rpm;appimage 扇出
# → docker debian/ubuntu/fedora 容器 extract-and-run 冒烟 → 直 API NuGet 消费。
# 用法: bash tests/Linux.AppImage.Integration/Verify.sh
# 需要 Linux 宿主与 dotnet SDK；产物仅在 artifacts/linux-appimage-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/linux-appimage-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerAppImageIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/AppImage.Api.PackageFixture/AppImage.Api.PackageFixture.csproj"
package_cache="$integration_root/nuget-cache"
extract_root="$integration_root/extract"
identity="BundlerLinuxAppImageIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Linux" ]] || fail "This integration test requires a Linux host."
for tool in dotnet sha256sum unzip od; do
    command -v "$tool" >/dev/null || fail "$tool is unavailable on this host."
done
# 可选断言工具：缺失则记 SKIP（docker/desktop-file-validate）。
optional_tools="docker desktop-file-validate"
for tool in $optional_tools; do
    if command -v "$tool" >/dev/null; then
        log "optional tool present: $tool"
    else
        log "SKIP: optional tool missing: $tool"
    fi
done
have_docker=0
command -v docker >/dev/null && docker info >/dev/null 2>&1 && have_docker=1

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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.AppImage; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 AppImage 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.AppImage.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the AppImage backend assembly."
# AppImage 后端包内必须内嵌 appimagetool 与两个 runtime 及许可证文件。
unzip -l "$package_dir/DotNet.Bundler.AppImage.$version.nupkg" > "$integration_root/appimage-package.list" \
    || fail "Cannot list the DotNet.Bundler.AppImage package."
grep -q "LICENSE-appimagetool" "$integration_root/appimage-package.list" \
    || fail "AppImage package is missing the appimagetool license."

publish_fixture() {
    # $1: 输出子目录；其余参数透传为 -p:BundlerTestAppImage* 等覆盖。
    local name="$1"; shift
    dotnet publish "$fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$integration_root/$name" \
        --packages "$package_cache" "$@"
}

# $1: .AppImage 路径, $2: 提取目标子目录 → 填充 $extract_root/$2/squashfs-root
appimage_extract() {
    local appimage="$1" sub="$2"
    rm -rf "$extract_root/$sub" && mkdir -p "$extract_root/$sub"
    (cd "$extract_root/$sub" && "$appimage" --appimage-extract >/dev/null)
    [[ -d "$extract_root/$sub/squashfs-root" ]] || fail "--appimage-extract produced no squashfs-root."
}

assert_appdir_shape() {
    # $1: squashfs-root 目录, $2: 包名, $3: 主可执行名
    local root="$1" pkg="$2" exe="$3"
    [[ -x "$root/AppRun" ]] || fail "$pkg: AppRun missing or not executable."
    [[ -L "$root/$pkg.desktop" ]] || fail "$pkg: root .desktop is not a symlink."
    [[ "$(readlink "$root/$pkg.desktop")" == "usr/share/applications/$pkg.desktop" ]] \
        || fail "$pkg: root .desktop symlink target mismatch: $(readlink "$root/$pkg.desktop")"
    [[ -f "$root/usr/share/applications/$pkg.desktop" ]] || fail "$pkg: usr/share .desktop missing."
    [[ -f "$root/$pkg.png" ]] || fail "$pkg: root <pkg>.png missing."
    [[ -f "$root/.DirIcon" ]] || fail "$pkg: .DirIcon missing."
    [[ -x "$root/usr/lib/$pkg/$exe" ]] || fail "$pkg: payload executable missing under usr/lib."
    [[ -L "$root/usr/bin/$pkg" ]] || fail "$pkg: usr/bin link missing."
    [[ -f "$root/usr/share/icons/hicolor/48x48/apps/$pkg.png" ]] \
        || fail "$pkg: hicolor icon missing."
    [[ -f "$root/usr/share/metainfo/$pkg.metainfo.xml" ]] \
        || fail "$pkg: metainfo missing."
    [[ -f "$root/usr/lib/$pkg/docs/readme.txt" ]] || fail "$pkg: BundlerResource payload missing."
}

log "== publishing the fixture (BundlerFormats=appimage) =="
publish_fixture default >/dev/null
appimage="$(find "$integration_root/default/linux-x64/appimage" -name '*.AppImage' | head -n1)"
[[ -n "$appimage" ]] || fail "No .AppImage artifact under the default publish output."
[[ "$appimage" == *"bundler-appimage-fixture_1.0.0_amd64.AppImage" ]] \
    || fail "Unexpected .AppImage name: $(basename "$appimage")"
[[ -f "$appimage.sha256" ]] || fail "Missing sha256 sidecar."
(cd "$(dirname "$appimage")" && sha256sum -c "$(basename "$appimage").sha256") \
    || fail "sha256 sidecar mismatch."
# 产物是 ELF 且可执行。
[[ -x "$appimage" ]] || fail ".AppImage is not executable."
[[ "$(od -An -tx1 -N4 "$appimage" | tr -d ' ')" == "7f454c46" ]] || fail ".AppImage lacks ELF magic."
[[ "$(od -An -tx1 -j18 -N2 "$appimage" | tr -d ' ')" == "3e00" ]] \
    || fail "x86_64 build: ELF e_machine must be 0x3e."

log "== --appimage-extract structure assertions =="
appimage_extract "$appimage" default
assert_appdir_shape "$extract_root/default/squashfs-root" \
    "bundler-appimage-fixture" "BundlerAppImageIntegrationFixture"
head -n1 "$extract_root/default/squashfs-root/AppRun" | grep -qx '#!/bin/sh' \
    || fail "AppRun must be a sh script."
grep -q 'usr/bin/bundler-appimage-fixture' "$extract_root/default/squashfs-root/AppRun" \
    || fail "AppRun must exec through the usr/bin link."
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$extract_root/default/squashfs-root/usr/share/applications/bundler-appimage-fixture.desktop" \
        || fail "generated .desktop fails desktop-file-validate."
else
    log "SKIP: desktop-file-validate missing."
fi

log "== running the extracted AppRun and the whole image =="
out="$(cd "$extract_root/default/squashfs-root" && ./AppRun hello world)"
[[ "$out" == "BundlerAppImageIntegrationFixture:hello,world" ]] \
    || fail "Extracted AppRun output mismatch: $out"
out="$(cd "$(dirname "$appimage")" && ./"$(basename "$appimage")" --appimage-extract-and-run hi)"
[[ "$out" == "BundlerAppImageIntegrationFixture:hi" ]] \
    || fail "--appimage-extract-and-run output mismatch: $out"
log "payload runs correctly (extract + extract-and-run)"

log "== override variant (name/version/bin-link/install-root/icon) =="
publish_fixture overrides \
    -p:BundlerTestAppImagePackageName="Custom AppImage" \
    -p:BundlerTestAppImageVersion="9.9.9-rc.1" \
    -p:BundlerTestAppImageBinLink="custom-link" \
    -p:BundlerTestAppImageInstallRoot="opt/custom" \
    -p:BundlerTestAppImageIconFile="$script_dir/Fixture/Assets/icon48.png" >/dev/null
ovr="$(find "$integration_root/overrides/linux-x64/appimage" -name '*.AppImage' | head -n1)"
[[ "$ovr" == *"custom-appimage_9.9.9-rc.1_amd64.AppImage" ]] \
    || fail "override naming mismatch: $(basename "$ovr")"
appimage_extract "$ovr" overrides
oroot="$extract_root/overrides/squashfs-root"
[[ -x "$oroot/opt/custom/BundlerAppImageIntegrationFixture" ]] \
    || fail "custom install root payload missing."
[[ -L "$oroot/usr/bin/custom-link" ]] || fail "custom bin link missing."
grep -q 'usr/bin/custom-link' "$oroot/AppRun" || fail "AppRun must exec the custom link."
[[ -f "$oroot/custom-appimage.png" && -f "$oroot/.DirIcon" ]] || fail "custom icon missing."

log "== desktop-file override variant =="
publish_fixture desktop \
    -p:BundlerTestAppImageDesktopFile="$script_dir/Fixture/Assets/custom.desktop" >/dev/null
dtop="$(find "$integration_root/desktop/linux-x64/appimage" -name '*.AppImage' | head -n1)"
appimage_extract "$dtop" desktop
droot="$extract_root/desktop/squashfs-root"
grep -q "Name=Bundler AppImage Fixture Custom" \
    "$droot/usr/share/applications/bundler-appimage-fixture.desktop" \
    || fail "overridden .desktop content missing."
[[ "$(readlink "$droot/bundler-appimage-fixture.desktop")" == \
   "usr/share/applications/bundler-appimage-fixture.desktop" ]] \
    || fail "root .desktop symlink must still point at the staged file."

log "== failure variant: unsupported architecture must fail publish =="
if publish_fixture bad-arch -p:BundlerTestAppImageArchitecture="ppc64" >/dev/null 2>&1; then
    fail "Architecture=ppc64 must fail the publish."
else
    log "bad-arch publish correctly failed."
fi

log "== linux-arm64 structural variant (cannot execute on x86_64) =="
dotnet publish "$fixture_project" -c Release -r linux-arm64 \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/arm64" \
    --packages "$package_cache" >/dev/null
arm="$(find "$integration_root/arm64/linux-arm64/appimage" -name '*.AppImage' | head -n1)"
[[ "$arm" == *"_aarch64.AppImage" ]] || fail "aarch64 naming mismatch: $(basename "$arm")"
[[ "$(od -An -tx1 -N4 "$arm" | tr -d ' ')" == "7f454c46" ]] || fail "aarch64 build lacks ELF magic."
[[ "$(od -An -tx1 -j18 -N2 "$arm" | tr -d ' ')" == "b700" ]] \
    || fail "aarch64 build: ELF e_machine must be 0xb7 (EM_AARCH64)."
log "aarch64 AppImage produced with the embedded aarch64 runtime."

log "== multi-format fanout (deb;rpm;appimage in one publish) =="
publish_fixture fanout '-p:BundlerTestFormats=deb%3Brpm%3Bappimage' >/dev/null
[[ -n "$(find "$integration_root/fanout/linux-x64/deb" -name '*.deb' | head -n1)" ]] \
    || fail "fanout missing .deb."
[[ -n "$(find "$integration_root/fanout/linux-x64/rpm" -name '*.rpm' | head -n1)" ]] \
    || fail "fanout missing .rpm."
[[ -n "$(find "$integration_root/fanout/linux-x64/appimage" -name '*.AppImage' | head -n1)" ]] \
    || fail "fanout missing .AppImage."
log "deb;rpm;appimage fanout produced all three artifacts."

if [[ $have_docker -eq 1 ]]; then
    log "== docker extract-and-run matrix =="
    for image in debian:stable ubuntu:latest fedora:latest; do
        if ! docker image inspect "$image" >/dev/null 2>&1 && \
           ! docker pull -q "$image" >/dev/null 2>&1; then
            log "SKIP: docker image $image unavailable (pull failed)."
            continue
        fi
        result="$(docker run --rm -v "$appimage:/tmp/pkg.AppImage:ro" "$image" sh -c \
            'cp /tmp/pkg.AppImage /tmp/run.AppImage && chmod +x /tmp/run.AppImage && \
             cd /tmp && ./run.AppImage --appimage-extract-and-run docker-ok')"
        [[ "$result" == "BundlerAppImageIntegrationFixture:docker-ok" ]] \
            || fail "docker $image run mismatch: $result"
        log "docker matrix: $image PASS"
    done
else
    log "SKIP: docker unavailable; container run assertions skipped."
fi

if [[ "$(uname -m)" == "x86_64" ]]; then
    log "== direct API consumption (DotNet.Bundler.AppImage nupkg) =="
    APPIMAGE_API_FIXTURE_OUTPUT="$integration_root/api" \
    dotnet run --project "$api_fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        --packages "$package_cache" | tee "$integration_root/api.log"
    grep -q "OK: " "$integration_root/api.log" || fail "API fixture did not produce an .AppImage."
fi

log "ALL CHECKS PASSED (LINUX-APPIMAGE-1)"
