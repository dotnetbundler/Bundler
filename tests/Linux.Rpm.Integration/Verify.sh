#!/usr/bin/env bash
# LINUX-RPM-1 .rpm 集成验证：真实 .NET payload → BundlerFormats=rpm → rpm -qip/rpm -qp --qf 元数据断言
# → rpm2cpio|cpio 载荷清单核对 → sha256 校验 → docker fedora 容器真实 rpm -i/rpm -e → deb;rpm 扇出断言。
# 用法: bash tests/Linux.Rpm.Integration/Verify.sh
# 需要 Linux 宿主与 dotnet SDK；产物仅在 artifacts/linux-rpm-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/linux-rpm-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerRpmIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/Rpm.Api.PackageFixture/Rpm.Api.PackageFixture.csproj"
api_output="$integration_root/api"
package_cache="$integration_root/nuget-cache"
extract_root="$integration_root/extract"
identity="BundlerLinuxRpmIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Linux" ]] || fail "This integration test requires a Linux host."
for tool in dotnet sha256sum unzip gzip; do
    command -v "$tool" >/dev/null || fail "$tool is unavailable on this host."
done
# 可选断言工具：缺失则记 SKIP（rpm/rpm2cpio/cpio/rpmlint/docker）。
optional_tools="rpm rpm2cpio cpio rpmlint docker"
for tool in $optional_tools; do
    if command -v "$tool" >/dev/null; then
        log "optional tool present: $tool"
    else
        log "SKIP: optional tool missing: $tool"
    fi
done
have_rpm=0; have_cpio=0; have_docker=0
command -v rpm >/dev/null && have_rpm=1
command -v rpm2cpio >/dev/null && command -v cpio >/dev/null && have_cpio=1
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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.Rpm; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 rpm 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.Rpm.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the Rpm backend assembly."

publish_fixture() {
    # $1: 输出子目录；其余参数透传为 -p:BundlerTestRpm* 等覆盖。
    local name="$1"; shift
    dotnet publish "$fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$integration_root/$name" \
        --packages "$package_cache" "$@"
}

rpm_field() {
    # $1: .rpm 路径, $2: queryformat tag（如 NAME、VERSION-RELEASE、ARCH）。
    rpm -qp --qf "%{$2}" "$1" 2>/dev/null || rpm -qp --qf "%{$2}" "$1" | tr -d '\n'
}

rpm_extract() {
    # $1: .rpm 路径, $2: 目标子目录。
    local rpm_path="$1" sub="$2"
    rm -rf "$extract_root/$sub" && mkdir -p "$extract_root/$sub"
    # GNU cpio 不为符号链接自动创建缺失的父目录（真实 rpm -i 无此问题，
    # 目标系统上 /usr/bin 本就存在），提取前按清单预建符号链接父目录。
    local link_path
    rpm -qplv "$rpm_path" | awk '$1 ~ /^l/ { print $(NF-2) }' | while read -r link_path; do
        mkdir -p "$extract_root/$sub$(dirname "$link_path")"
    done
    (cd "$extract_root/$sub" && rpm2cpio "$rpm_path" | cpio -idm --quiet --no-absolute-filenames)
}

log "== publishing the fixture (BundlerFormats=rpm) =="
publish_fixture default >/dev/null
rpm_path="$(find "$integration_root/default/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$rpm_path" ]] || fail "No .rpm artifact under the default publish output."
[[ "$rpm_path" == *"bundler-rpm-fixture-1.0.0-1.x86_64.rpm" ]] \
    || fail "Unexpected .rpm name: $(basename "$rpm_path")"
[[ -f "$rpm_path.sha256" ]] || fail "Missing sha256 sidecar."
(cd "$(dirname "$rpm_path")" && sha256sum -c "$(basename "$rpm_path").sha256") \
    || fail "sha256 sidecar mismatch."

if [[ $have_rpm -eq 1 ]]; then
    log "== rpm -qip metadata assertions =="
    rpm -qip "$rpm_path" > "$integration_root/qip.txt" || fail "rpm -qip failed."
    info="$(cat "$integration_root/qip.txt")"
    case "$info" in *"Name        : bundler-rpm-fixture"*) ;; *) fail "rpm -qip NAME mismatch.";; esac
    case "$info" in *"Version     : 1.0.0"*) ;; *) fail "rpm -qip VERSION mismatch.";; esac
    case "$info" in *"Release     : 1"*) ;; *) fail "rpm -qip RELEASE mismatch.";; esac
    case "$info" in *"Architecture: x86_64"*) ;; *) fail "rpm -qip ARCH mismatch.";; esac
    [[ "$(rpm_field "$rpm_path" PAYLOADFORMAT)" == "cpio" ]] || fail "PAYLOADFORMAT != cpio."
    [[ "$(rpm_field "$rpm_path" PAYLOADCOMPRESSOR)" == "gzip" ]] || fail "PAYLOADCOMPRESSOR != gzip."
    [[ "$(rpm_field "$rpm_path" VENDOR)" == "DotNet.Bundler Tests" ]] || fail "VENDOR mismatch."
    # 自依赖与 rpmlib 依赖。
    provides="$(rpm -qp --provides "$rpm_path" || true)"
    case "$provides" in *"bundler-rpm-fixture = 1.0.0-1"*) ;; *) fail "Self provide missing: $provides";; esac
    requires="$(rpm -qp --requires "$rpm_path" || true)"
    case "$requires" in *"rpmlib(CompressedFileNames)"*) ;; *) fail "rpmlib requires missing: $requires";; esac
    # 文件清单须含 install root 目录条目与 bin 符号链接。
    files="$(rpm -qplv "$rpm_path" || true)"
    case "$files" in *"drwxr-xr-x"*"/usr/lib/bundler-rpm-fixture"*) ;; *) fail "Install root dir entry missing: $files";; esac
    case "$files" in *"/usr/bin/bundler-rpm-fixture -> "*) ;; *) fail "bin symlink missing: $files";; esac
else
    log "SKIP: host rpm not available; -qip assertions skipped."
fi

if [[ $have_cpio -eq 1 ]]; then
    log "== cpio payload assertions =="
    rpm_extract "$rpm_path" payload
    [[ -x "$extract_root/payload/usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture" ]] \
        || fail "Executable missing from the payload."
    [[ -f "$extract_root/payload/usr/lib/bundler-rpm-fixture/docs/readme.txt" ]] \
        || fail "Resource file missing from the payload."
    [[ -L "$extract_root/payload/usr/bin/bundler-rpm-fixture" ]] \
        || fail "/usr/bin symlink missing."
    link="$(readlink "$extract_root/payload/usr/bin/bundler-rpm-fixture")"
    [[ "$link" == "../lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture" ]] \
        || fail "Unexpected symlink target: $link"
    out="$(cd "$extract_root/payload" && ./usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture hello)"
    [[ "$out" == *"BundlerRpmIntegrationFixture:hello"* ]] || fail "Payload binary did not run: $out"
else
    log "SKIP: rpm2cpio+cpio not available; payload assertions skipped."
fi

log "== variant: overrides =="
publish_fixture overrides >/dev/null \
    -p:BundlerTestRpmPackageName=my-rpm-app \
    -p:BundlerTestRpmVersion=9.9 \
    -p:BundlerTestRpmRelease=7.el9 \
    -p:BundlerTestRpmEpoch=2 \
    -p:BundlerTestRpmArchitecture=noarch \
    -p:BundlerTestRpmInstallRoot=/opt/myapp \
    -p:BundlerTestRpmBinLink=none
ovr="$(find "$integration_root/overrides/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$ovr" ]] && [[ "$(basename "$ovr")" == "my-rpm-app-9.9-7.el9.noarch.rpm" ]] \
    || fail "Override variant produced an unexpected name: $ovr"
if [[ $have_rpm -eq 1 ]]; then
    [[ "$(rpm_field "$ovr" EPOCHNUM)" == "2" ]] || fail "EPOCH override did not land."
    [[ "$(rpm_field "$ovr" ARCH)" == "noarch" ]] || fail "ARCH override did not land."
    files="$(rpm -qplv "$ovr" || true)"
    case "$files" in *"/opt/myapp/"*) ;; *) fail "InstallRoot override did not land: $files";; esac
    case "$files" in *"/usr/bin/"*) fail "BinLink=none must emit no /usr/bin entry.";; *) ;; esac
fi

log "== variant: SemVer prerelease → rpm Release =="
publish_fixture prerelease >/dev/null -p:BundlerVersion=1.0.0-alpha.2
pre="$(find "$integration_root/prerelease/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ "$(basename "$pre")" == "bundler-rpm-fixture-1.0.0-0.1.alpha.2.x86_64.rpm" ]] \
    || fail "Prerelease mapping produced an unexpected name: $(basename "$pre")"
if [[ $have_rpm -eq 1 ]]; then
    [[ "$(rpm_field "$pre" VERSION)" == "1.0.0" ]] || fail "Prerelease VERSION mismatch."
    [[ "$(rpm_field "$pre" RELEASE)" == "0.1.alpha.2" ]] || fail "Prerelease RELEASE mismatch."
fi

log "== variant: failure leaves no artifact =="
if publish_fixture failure >/dev/null 2>&1 -p:BundlerTestRpmPackageName='Bad Name'; then
    fail "An invalid package name must fail the publish."
fi
[[ -z "$(find "$integration_root/failure" -name '*.rpm' 2>/dev/null)" ]] \
    || fail "A failed build left a .rpm behind."

log "== variant: deb;rpm multi-format fanout =="
publish_fixture fanout >/dev/null -p:BundlerTestFormats="deb%3Brpm"
fan_rpm="$(find "$integration_root/fanout/linux-x64/rpm" -name '*.rpm' 2>/dev/null | head -n1)"
fan_deb="$(find "$integration_root/fanout/linux-x64/deb" -name '*.deb' 2>/dev/null | head -n1)"
[[ -n "$fan_rpm" && -n "$fan_deb" ]] \
    || fail "deb;rpm fanout must produce both artifacts (rpm=$fan_rpm deb=$fan_deb)."

if [[ $have_docker -eq 1 ]]; then
    log "== docker install/remove matrix =="
    for image in fedora:latest; do
        docker run --rm -v "$rpm_path:/tmp/pkg.rpm:ro" "$image" sh -c '
            set -e
            rpm -i /tmp/pkg.rpm
            rpm -q bundler-rpm-fixture | grep -q bundler-rpm-fixture
            rpm -ql bundler-rpm-fixture | grep -q /usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture
            /usr/bin/bundler-rpm-fixture probe | grep -q probe
            rpm -V bundler-rpm-fixture
            rpm -e bundler-rpm-fixture
            test ! -e /usr/lib/bundler-rpm-fixture
            test ! -L /usr/bin/bundler-rpm-fixture
        ' || fail "rpm -i/-e failed in $image."
        log "docker matrix: $image PASS"
    done
else
    log "SKIP: docker unavailable; container install assertions skipped."
fi

if command -v rpmlint >/dev/null; then
    log "== rpmlint report (informational until the RPM-3 baseline) =="
    rpmlint "$rpm_path" || log "note: rpmlint reported findings (baseline lands in RPM-3)."
fi

log "PASS: LINUX-RPM-1 integration checks complete."
