#!/usr/bin/env bash
# LINUX-RPM-1/2 .rpm 集成验证：真实 .NET payload → BundlerFormats=rpm → rpm -qip/--queryformat 逐字段断言
# （关系字段三件套、LICENSE/GROUP/URL、FILEFLAGS %doc/%license）→ desktop-file-validate
# → rpm2cpio|cpio 载荷清单核对 → sha256 校验 → docker fedora 容器真实 rpm -i/rpm -e + rpm -ql → deb;rpm 扇出断言。
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
# 可选断言工具：缺失则记 SKIP（rpm/rpm2cpio/cpio/rpmlint/docker/desktop-file-validate）。
optional_tools="rpm rpm2cpio cpio rpmlint docker desktop-file-validate"
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

    log "== RPM-2 freedesktop/doc/files 落位断言 =="
    filelist="$(rpm -qpl "$rpm_path" || true)"
    for expected in \
        "/usr/share/applications/bundler-rpm-fixture.desktop" \
        "/usr/share/icons/hicolor/48x48/apps/bundler-rpm-fixture.png" \
        "/usr/share/icons/hicolor/48x48@2/apps/bundler-rpm-fixture.png" \
        "/usr/share/metainfo/bundler-rpm-fixture.metainfo.xml" \
        "/usr/share/doc/bundler-rpm-fixture/changelog.gz" \
        "/usr/share/licenses/bundler-rpm-fixture/LICENSE.txt" \
        "/etc/bundler-rpm-fixture/defaults.conf"; do
        case "$filelist" in *"$expected"*) ;; *) fail "Missing payload path: $expected";; esac
    done
    # 共享系统目录不被包占有；包自有叶子目录须占有（目录条目按 rpm -qplv 的 d 行识别，
    # rpm 的 FILENAMES 对目录不带尾斜杠）。
    dirs="$(rpm -qplv "$rpm_path" | awk '$1 ~ /^d/ {print $NF}' || true)"
    case "$dirs" in *"/usr/share/licenses/bundler-rpm-fixture"*) ;; *) fail "licenses/<pkg> dir not owned: $dirs";; esac
    case "$dirs" in *"/usr/share/doc/bundler-rpm-fixture"*) ;; *) fail "doc/<pkg> dir not owned: $dirs";; esac
    case "$dirs" in *"/etc/bundler-rpm-fixture"*) ;; *) fail "/etc/<pkg> dir not owned: $dirs";; esac
    for shared in "/etc" "/usr/share" "/usr/share/applications" "/usr/share/doc" "/usr/share/icons" "/usr/share/metainfo"; do
        if printf '%s\n' "$dirs" | grep -qx "$shared"; then
            fail "Shared dir must not be owned: $shared"
        fi
    done
    # FILEFLAGS：LICENSE=128、DOC=2 按路径落位。
    flags="$(rpm -qp --queryformat "[%{FILENAMES} %{FILEFLAGS}\n]" "$rpm_path" || true)"
    case "$flags" in *"/usr/share/licenses/bundler-rpm-fixture/LICENSE.txt 128"*) ;; *) fail "License file must carry flag 128: $flags";; esac
    case "$flags" in *"/usr/share/doc/bundler-rpm-fixture/changelog.gz 2"*) ;; *) fail "changelog.gz must carry %doc flag 2: $flags";; esac
    # LICENSE/GROUP/URL 默认：Group=Unspecified、URL=BundlerHomepage。
    [[ "$(rpm_field "$rpm_path" GROUP)" == "Unspecified" ]] || fail "Default GROUP mismatch."
    [[ "$(rpm_field "$rpm_path" URL)" == "https://example.com/rpm-fixture" ]] || fail "URL must default to Homepage."
else
    log "SKIP: host rpm not available; -qip assertions skipped."
fi

if command -v desktop-file-validate >/dev/null && [[ $have_cpio -eq 1 ]]; then
    log "== desktop-file-validate on the generated .desktop =="
    [[ -d "$extract_root/payload" ]] || rpm_extract "$rpm_path" payload
    desktop-file-validate "$extract_root/payload/usr/share/applications/bundler-rpm-fixture.desktop" \
        || fail "The generated .desktop fails desktop-file-validate."
else
    log "SKIP: desktop-file-validate or cpio missing; .desktop validation skipped."
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

log "== variant: relation clauses + metadata overrides =="
publish_fixture metadata >/dev/null \
    -p:BundlerTestRpmRequires='libpng%3Bzlib >= 1.2' \
    -p:BundlerTestRpmProvides='bundler-plugin = 2.0' \
    -p:BundlerTestRpmConflicts='old-bundler' \
    -p:BundlerTestRpmObsoletes='bundler-rpm-legacy < 1.0' \
    -p:BundlerTestRpmRecommends='bundler-extras' \
    -p:BundlerTestRpmSuggests='bundler-docs >= 0.9' \
    -p:BundlerTestRpmLicense='MIT OR Apache-2.0' \
    -p:BundlerTestRpmGroup='Applications/Engineering' \
    -p:BundlerTestRpmUrl='https://example.com/rpm-override'
meta="$(find "$integration_root/metadata/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$meta" ]] || fail "The metadata variant produced no .rpm."
if [[ $have_rpm -eq 1 ]]; then
    req="$(rpm -qp --requires "$meta" || true)"
    case "$req" in *"libpng"*) ;; *) fail "Requires libpng missing: $req";; esac
    case "$req" in *"zlib >= 1.2"*|*"zlib  >=  1.2"*) ;; *) fail "Requires 'zlib >= 1.2' missing: $req";; esac
    prov="$(rpm -qp --provides "$meta" || true)"
    case "$prov" in *"bundler-plugin = 2.0"*|*"bundler-plugin  =  2.0"*) ;; *) fail "Provides missing: $prov";; esac
    con="$(rpm -qp --conflicts "$meta" || true)"
    case "$con" in *"old-bundler"*) ;; *) fail "Conflicts missing: $con";; esac
    obs="$(rpm -qp --obsoletes "$meta" || true)"
    case "$obs" in *"bundler-rpm-legacy"*) ;; *) fail "Obsoletes missing: $obs";; esac
    rec="$(rpm -qp --recommends "$meta" || true)"
    case "$rec" in *"bundler-extras"*) ;; *) fail "Recommends missing: $rec";; esac
    sug="$(rpm -qp --suggests "$meta" || true)"
    case "$sug" in *"bundler-docs"*) ;; *) fail "Suggests missing: $sug";; esac
    [[ "$(rpm_field "$meta" LICENSE)" == "MIT OR Apache-2.0" ]] || fail "LICENSE override missing."
    [[ "$(rpm_field "$meta" GROUP)" == "Applications/Engineering" ]] || fail "GROUP override missing."
    [[ "$(rpm_field "$meta" URL)" == "https://example.com/rpm-override" ]] || fail "URL override missing."
fi

log "== variant: caller-supplied .desktop override =="
publish_fixture desktop-override >/dev/null \
    -p:BundlerTestRpmDesktopFile="$script_dir/Fixture/Assets/custom.desktop"
ovd="$(find "$integration_root/desktop-override/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$ovd" ]] || fail "The desktop-override variant produced no .rpm."
if [[ $have_cpio -eq 1 ]]; then
    rpm_extract "$ovd" desktop-override
    grep -q "Name=Bundler Rpm Fixture Custom" \
        "$extract_root/desktop-override/usr/share/applications/bundler-rpm-fixture.desktop" \
        || fail "The custom .desktop did not replace the generated one."
    if command -v desktop-file-validate >/dev/null; then
        desktop-file-validate \
            "$extract_root/desktop-override/usr/share/applications/bundler-rpm-fixture.desktop" \
            || fail "The override .desktop fails validation."
    fi
fi

log "== variant: failure leaves no artifact =="
if publish_fixture failure >/dev/null 2>&1 -p:BundlerTestRpmPackageName='Bad Name'; then
    fail "An invalid package name must fail the publish."
fi
[[ -z "$(find "$integration_root/failure" -name '*.rpm' 2>/dev/null)" ]] \
    || fail "A failed build left a .rpm behind."

log "== variant: invalid dependency clause and bad Files destination =="
if publish_fixture baddep >/dev/null 2>&1 -p:BundlerTestRpmRequires='foo != 1.0'; then
    fail "An invalid dependency operator must fail the publish."
fi
if publish_fixture badfile >/dev/null 2>&1 -p:BundlerTestRpmBadFile=1; then
    fail "A relative RpmFile destination must fail the publish."
fi

log "== variant: scriptlets + systemd unit =="
publish_fixture scripts >/dev/null -p:BundlerTestRpmScripts=1 -p:BundlerTestRpmSystemd=1
scr="$(find "$integration_root/scripts/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$scr" ]] || fail "The scripts variant produced no .rpm."
if [[ $have_rpm -eq 1 ]]; then
    [[ "$(rpm_field "$scr" PREINPROG)" == "/bin/sh" ]] \
        || fail "PREINPROG must be /bin/sh: $(rpm_field "$scr" PREINPROG)"
    rpm -qp --scripts "$scr" | grep -q "echo prein" || fail "PREIN scriptlet missing."
    rpm -qp --scripts "$scr" | grep -q "echo postun" || fail "POSTUN scriptlet missing."
    rpm -qp --scripts "$scr" | grep -q "daemon-reload" \
        || fail "The systemd daemon-reload epilogue missing from scriptlets."
fi

log "== variant: %config(noreplace) and compression =="
publish_fixture configx >/dev/null -p:BundlerTestRpmConfigFiles='/usr/lib/bundler-rpm-fixture/docs/readme.txt'
cfg="$(find "$integration_root/configx/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$cfg" ]] || fail "The config variant produced no .rpm."
# 升级语义需要不同 EVR：同包 Release=2 作为升级目标。
publish_fixture configx-v2 >/dev/null \
    -p:BundlerTestRpmConfigFiles='/usr/lib/bundler-rpm-fixture/docs/readme.txt' \
    -p:BundlerTestRpmRelease=2
cfg_v2="$(find "$integration_root/configx-v2/linux-x64/rpm" -name '*.rpm' | head -n1)"
[[ -n "$cfg_v2" ]] || fail "The config v2 variant produced no .rpm."
if [[ $have_rpm -eq 1 ]]; then
    ff="$(rpm -qp --queryformat '[%{FILENAMES} %{FILEFLAGS}\n]' "$cfg" || true)"
    echo "$ff" | grep -E '/etc/bundler-rpm-fixture/defaults\.conf +17' \
        || fail "/etc file must carry flags 17 (config|noreplace): $ff"
    echo "$ff" | grep -E '/usr/lib/bundler-rpm-fixture/docs/readme\.txt +17' \
        || fail "Explicit ConfigFiles path must carry flags 17: $ff"
    [[ "$(rpm_field "$cfg" PAYLOADCOMPRESSOR)" == "gzip" ]] \
        || fail "PAYLOADCOMPRESSOR must be gzip."
fi

log "== variant: failure — bad scriptlet and compression =="
if publish_fixture badscript >/dev/null 2>&1 -p:BundlerTestRpmCrlfScript=1; then
    fail "A CRLF scriptlet must fail the publish."
fi
if publish_fixture badcfg >/dev/null 2>&1 -p:BundlerTestRpmConfigFiles='/usr/lib/bundler-rpm-fixture/missing.conf'; then
    fail "A ConfigFiles path outside the payload must fail the publish."
fi
if publish_fixture badcomp >/dev/null 2>&1 -p:BundlerTestRpmCompression='xz'; then
    fail "A non-gzip compression must fail the publish."
fi

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
            # RPM-2 落位：装后逐路径断言 freedesktop/doc/license/etc 文件。
            for path in \
                /usr/share/applications/bundler-rpm-fixture.desktop \
                /usr/share/icons/hicolor/48x48/apps/bundler-rpm-fixture.png \
                /usr/share/metainfo/bundler-rpm-fixture.metainfo.xml \
                /usr/share/doc/bundler-rpm-fixture/changelog.gz \
                /usr/share/licenses/bundler-rpm-fixture/LICENSE.txt \
                /etc/bundler-rpm-fixture/defaults.conf; do
                rpm -ql bundler-rpm-fixture | grep -qx "$path"
                test -e "$path"
            done
            # %doc 标记经 rpm 查询可见。
            rpm -qd bundler-rpm-fixture | grep -q changelog.gz
            rpm -V bundler-rpm-fixture
            rpm -e bundler-rpm-fixture
            test ! -e /usr/lib/bundler-rpm-fixture
            test ! -L /usr/bin/bundler-rpm-fixture
            # 卸载须带走包自有叶子目录。
            test ! -e /usr/share/licenses/bundler-rpm-fixture
            test ! -e /usr/share/doc/bundler-rpm-fixture
            test ! -e /etc/bundler-rpm-fixture
        ' || fail "rpm -i/-e failed in $image."
        log "docker matrix: $image PASS"
    done

    if [[ -n "$scr" && -n "$cfg" ]]; then
        log "== docker: scriptlet markers, %config .rpmsave, rpm -U upgrade =="
        docker run --rm \
            -v "$scr:/tmp/scripts.rpm:ro" -v "$cfg:/tmp/config.rpm:ro" \
            -v "$cfg_v2:/tmp/config-v2.rpm:ro" \
            fedora:latest sh -c '
                set -e
                rpm -i /tmp/scripts.rpm
                grep -qx prein /tmp/bundler-rpm-scripts.log
                grep -qx postin /tmp/bundler-rpm-scripts.log
                grep -c . /tmp/bundler-rpm-scripts.log | grep -qx 2
                test -f /usr/lib/systemd/system/bundler-rpm-fixture.service
                rpm -e bundler-rpm-fixture
                grep -qx preun /tmp/bundler-rpm-scripts.log
                grep -qx postun /tmp/bundler-rpm-scripts.log
                # %config(noreplace): 本地修改的 /etc 配置在卸载时保留为 .rpmsave。
                rpm -i /tmp/config.rpm
                echo changed > /etc/bundler-rpm-fixture/defaults.conf
                rpm -e bundler-rpm-fixture
                test -f /etc/bundler-rpm-fixture/defaults.conf.rpmsave
                grep -qx changed /etc/bundler-rpm-fixture/defaults.conf.rpmsave
                # rpm -U 真实升级（1.0.0-1 → 1.0.0-2）：修改过的 %config(noreplace)
                # 原地保留、不产生 .rpmnew。
                rpm -i /tmp/config.rpm
                echo upgraded > /etc/bundler-rpm-fixture/defaults.conf
                rpm -U /tmp/config-v2.rpm
                rpm -q bundler-rpm-fixture | grep -q 1.0.0-2
                grep -qx upgraded /etc/bundler-rpm-fixture/defaults.conf
                test ! -e /etc/bundler-rpm-fixture/defaults.conf.rpmnew
                rpm -e bundler-rpm-fixture
            ' || fail "Scriptlet/config/upgrade semantics failed in fedora:latest."
        log "docker: scriptlets + %config + rpm -U PASS"
    fi
else
    log "SKIP: docker unavailable; container install assertions skipped."
fi

if command -v rpmlint >/dev/null; then
    log "== rpmlint report (informational until the RPM-3 baseline) =="
    rpmlint "$rpm_path" || log "note: rpmlint reported findings (baseline lands in RPM-4)."
fi

log "PASS: LINUX-RPM-1..3 integration checks complete."
