#!/usr/bin/env bash
# LINUX-DEB-1/2 .deb 集成验证：真实 .NET payload → BundlerFormats=deb → ar/dpkg-deb 结构断言
# → dpkg-deb -I/-c 元数据与内容清单核对 → md5sums/sha256 校验 → sudo dpkg -i/-r 真实装卸。
# 用法: bash tests/Linux.Deb.Integration/Verify.sh
# 需要 Linux 宿主（dpkg/apt 系）与 dotnet SDK；产物仅在 artifacts/linux-deb-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/linux-deb-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerDebIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/Deb.Api.PackageFixture/Deb.Api.PackageFixture.csproj"
api_output="$integration_root/api"
package_cache="$integration_root/nuget-cache"
extract_root="$integration_root/extract"
identity="BundlerLinuxDebIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

[[ "$(uname -s)" == "Linux" ]] || fail "This integration test requires a Linux host."
for tool in dotnet ar tar md5sum sha256sum dpkg-deb unzip gzip; do
    command -v "$tool" >/dev/null || fail "$tool is unavailable on this host."
done
# 可选断言工具：缺失则记 SKIP（desktop-file-validate/lintian）。
optional_tools="desktop-file-validate lintian"

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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.Deb; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 deb 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.Deb.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the Deb backend assembly."

publish_fixture() {
    # $1: 输出子目录；其余参数透传为 -p:BundlerTestDeb* 等覆盖。
    local name="$1"; shift
    dotnet publish "$fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$integration_root/$name" \
        --packages "$package_cache" "$@"
}

deb_control() {
    # $1: .deb 路径；把 control.tar.gz 解到 $extract_root/control/$2。
    local deb="$1" sub="$2"
    rm -rf "$extract_root/control/$sub" && mkdir -p "$extract_root/control/$sub"
    dpkg-deb -e "$deb" "$extract_root/control/$sub"
}

deb_data() {
    # $1: .deb 路径；把 data 载荷解到 $extract_root/data/$2。
    local deb="$1" sub="$2"
    rm -rf "$extract_root/data/$sub" && mkdir -p "$extract_root/data/$sub"
    dpkg-deb --fsys-tarfile "$deb" | tar -xf - -C "$extract_root/data/$sub"
}

log "== publishing fixture (BundlerFormats=deb) =="
publish_fixture bundle >/dev/null

deb_path="$integration_root/bundle/linux-x64/deb/bundler-deb-fixture_1.0.0-1_amd64.deb"
[[ -f "$deb_path" ]] || fail "The .deb artifact is missing: $(find "$integration_root/bundle" -type f)"
[[ -f "$deb_path.sha256" ]] || fail "The .sha256 sidecar is missing."
log "produced: $deb_path"

log "== ar member listing =="
ar_listing="$(ar t "$deb_path")" || fail "ar t failed on the produced .deb."
printf '%s' "$ar_listing" | grep -q "debian-binary" || fail "ar listing lacks debian-binary."
printf '%s' "$ar_listing" | grep -q "control.tar.gz" || fail "ar listing lacks control.tar.gz."
printf '%s' "$ar_listing" | grep -q "data.tar.gz" || fail "ar listing lacks data.tar.gz."
dpkg-deb --fsys-tarfile "$deb_path" >/dev/null || fail "dpkg-deb cannot parse the archive."

log "== sha256 sidecar =="
(cd "$(dirname "$deb_path")" && sha256sum -c "$(basename "$deb_path").sha256") >/dev/null \
    || fail "The .sha256 sidecar does not match the .deb."

log "== control metadata =="
deb_control "$deb_path" default
control="$(cat "$extract_root/control/default/control")"
for field in "Package: bundler-deb-fixture" "Version: 1.0.0-1" "Architecture: amd64" \
    "Maintainer: DotNet.Bundler Tests <tests@example.com>" "Priority: optional" "Section: utils" \
    "Homepage: https://example.com/deb-fixture" "Installed-Size:" \
    "Description: Disposable .deb integration-test fixture."; do
    printf '%s\n' "$control" | grep -qF "$field" || fail "control lacks '$field': $(printf '%s\n' "$control")"
done
[[ -f "$extract_root/control/default/md5sums" ]] || fail "control archive lacks md5sums."
info="$(dpkg-deb -I "$deb_path")" || fail "dpkg-deb -I rejects the package."
printf '%s' "$info" | grep -q "new Debian package" || fail "dpkg-deb -I reports unexpected info."

log "== payload contents (dpkg-deb -c + extraction) =="
listing="$(dpkg-deb -c "$deb_path")" || fail "dpkg-deb -c failed."
printf '%s' "$listing" | grep -q "usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture" \
    || fail "Payload lacks usr/lib payload: $(printf '%s' "$listing")"
printf '%s' "$listing" | grep -q "usr/lib/bundler-deb-fixture/docs/readme.txt" \
    || fail "Payload lacks the BundlerResource at docs/readme.txt: $(printf '%s' "$listing")"
printf '%s' "$listing" | grep -q "usr/bin/bundler-deb-fixture -> ../lib/bundler-deb-fixture/BundlerDebIntegrationFixture" \
    || fail "Payload lacks the relative usr/bin symlink: $(printf '%s' "$listing")"
printf '%s' "$listing" | grep -qE "^-rwxr-xr-x .*usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture$" \
    || fail "The main executable lacks mode 0755: $(printf '%s' "$listing")"
printf '%s' "$listing" | grep -q "usr/share/applications/bundler-deb-fixture.desktop" \
    || fail "The payload listing lacks the .desktop file."

log "== desktop integration payload (.desktop/icons/metainfo/doc/DebFile) =="
deb_data "$deb_path" default
data_root="$extract_root/data/default"
desktop_file="$data_root/usr/share/applications/bundler-deb-fixture.desktop"
[[ -f "$desktop_file" ]] || fail "The generated .desktop file is missing."
desktop="$(cat "$desktop_file")"
for line in "Type=Application" "Name=Bundler Deb Fixture" \
    "Comment=Disposable .deb integration-test fixture." "Exec=bundler-deb-fixture %u" \
    "Icon=bundler-deb-fixture" "Terminal=false" "Categories=Utility;Development;" \
    "MimeType=application/x-bundler-fixture;x-scheme-handler/bdlfixture;"; do
    printf '%s\n' "$desktop" | grep -qxF "$line" \
        || fail "The .desktop file lacks '$line': $(printf '%s\n' "$desktop")"
done
[[ -f "$data_root/usr/share/icons/hicolor/48x48/apps/bundler-deb-fixture.png" ]] \
    || fail "The hicolor 48x48 icon is missing."
[[ -f "$data_root/usr/share/icons/hicolor/48x48@2/apps/bundler-deb-fixture.png" ]] \
    || fail "The hicolor @2 icon is missing."
[[ -f "$data_root/usr/share/metainfo/bundler-deb-fixture.metainfo.xml" ]] \
    || fail "The metainfo file is missing."
[[ -f "$data_root/usr/share/doc/bundler-deb-fixture/changelog.Debian.gz" ]] \
    || fail "The payload lacks the auto-generated changelog.Debian.gz."
zcat "$data_root/usr/share/doc/bundler-deb-fixture/changelog.Debian.gz" \
    | grep -q "bundler-deb-fixture (1.0.0-1) unstable" \
    || fail "changelog.Debian.gz lacks the package stanza."
[[ -f "$data_root/usr/share/doc/bundler-deb-fixture/copyright" ]] \
    || fail "The copyright file is missing."
grep -qx "MIT License" <(head -n1 "$data_root/usr/share/doc/bundler-deb-fixture/copyright") \
    || fail "The copyright content is wrong."
[[ -f "$data_root/usr/share/doc/bundler-deb-fixture/changelog.gz" ]] \
    || fail "changelog.gz is missing."
gzip -dc "$data_root/usr/share/doc/bundler-deb-fixture/changelog.gz" | grep -q "# Changelog" \
    || fail "changelog.gz does not contain the changelog."
[[ -f "$data_root/etc/bundler-deb-fixture/defaults.conf" ]] \
    || fail "The BundlerDebFile /etc entry is missing."
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$desktop_file" || fail "desktop-file-validate rejects the generated file."
else
    log "SKIP: desktop-file-validate unavailable on this host."
fi

app="$extract_root/data/default/usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture"
[[ -x "$app" ]] || fail "Extracted executable is not executable."
[[ "$("$app")" == BundlerDebIntegrationFixture:* ]] || fail "The extracted app did not run."
[[ -L "$extract_root/data/default/usr/bin/bundler-deb-fixture" ]] \
    || fail "usr/bin symlink is missing in the extracted tree."

log "== md5sums verification =="
(cd "$extract_root/data/default" && md5sum -c "$extract_root/control/default/md5sums" > md5.out) \
    || fail "md5sums do not match the extracted payload: $(cat "$extract_root/data/default/md5.out")"

log "== override variant (package name/version/root/link) =="
publish_fixture override \
    -p:BundlerTestDebPackageName=custom-fixture \
    -p:BundlerTestDebVersion="2:9.9.9-5" \
    -p:BundlerTestDebMaintainer="Custom Maintainer <m@example.com>" \
    -p:BundlerTestDebInstallRoot=/opt/custom-fixture \
    -p:BundlerTestDebBinLink=custom-fixture-cli >/dev/null
override_deb="$integration_root/override/linux-x64/deb/custom-fixture_9.9.9-5_amd64.deb"
[[ -f "$override_deb" ]] || fail "Override variant produced no custom-named .deb: $(find "$integration_root/override" -type f)"
deb_control "$override_deb" override
control="$(cat "$extract_root/control/override/control")"
for field in "Package: custom-fixture" "Version: 2:9.9.9-5" \
    "Maintainer: Custom Maintainer <m@example.com>"; do
    printf '%s\n' "$control" | grep -qF "$field" || fail "Override control lacks '$field'."
done
listing="$(dpkg-deb -c "$override_deb")"
printf '%s' "$listing" | grep -q "opt/custom-fixture/BundlerDebIntegrationFixture" \
    || fail "Override payload lacks the /opt install root."
printf '%s' "$listing" | grep -q "usr/bin/custom-fixture-cli -> /opt/custom-fixture/BundlerDebIntegrationFixture" \
    || fail "Override payload lacks the absolute usr/bin symlink."

log "== semver variant (prerelease mapping) =="
publish_fixture semver -p:BundlerVersion="2.5.0-beta.3+build.1" >/dev/null
semver_deb="$integration_root/semver/linux-x64/deb/bundler-deb-fixture_2.5.0~beta.3+build.1-1_amd64.deb"
[[ -f "$semver_deb" ]] || fail "SemVer variant produced no '~' mapped .deb: $(find "$integration_root/semver" -type f)"
deb_control "$semver_deb" semver
grep -qF "Version: 2.5.0~beta.3+build.1-1" "$extract_root/control/semver/control" \
    || fail "SemVer mapping missing in control: $(cat "$extract_root/control/semver/control")"

log "== metadata variant (relations/section/priority) =="
publish_fixture metadata \
    -p:BundlerTestDebDepends="libc6 (>= 2.35)%3Blibssl3" \
    -p:BundlerTestDebRecommends="ca-certificates" \
    -p:BundlerTestDebProvides="virtual-fixture" \
    -p:BundlerTestDebConflicts="legacy-fixture" \
    -p:BundlerTestDebReplaces="legacy-fixture" \
    -p:BundlerTestDebSection=utils \
    -p:BundlerTestDebPriority=extra >/dev/null
metadata_deb="$integration_root/metadata/linux-x64/deb/bundler-deb-fixture_1.0.0-1_amd64.deb"
[[ -f "$metadata_deb" ]] || fail "Metadata variant produced no .deb."
deb_control "$metadata_deb" metadata
control="$(cat "$extract_root/control/metadata/control")"
for field in "Depends: libc6 (>= 2.35), libssl3" "Recommends: ca-certificates" \
    "Provides: virtual-fixture" "Conflicts: legacy-fixture" "Replaces: legacy-fixture" \
    "Section: utils" "Priority: extra"; do
    printf '%s\n' "$control" | grep -qF "$field" \
        || fail "Metadata control lacks '$field': $(printf '%s\n' "$control")"
done

log "== desktop override variant (BundlerDebDesktopFile) =="
publish_fixture desktop-override \
    -p:BundlerTestDebDesktopFile="$script_dir/Fixture/Assets/custom.desktop" >/dev/null
desktop_deb="$integration_root/desktop-override/linux-x64/deb/bundler-deb-fixture_1.0.0-1_amd64.deb"
deb_data "$desktop_deb" desktop-override
custom_desktop="$extract_root/data/desktop-override/usr/share/applications/bundler-deb-fixture.desktop"
[[ -f "$custom_desktop" ]] || fail "The override .desktop is missing."
grep -qx "Name=Bundler Deb Fixture Custom" "$custom_desktop" \
    || fail "The override .desktop content differs: $(cat "$custom_desktop")"
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$custom_desktop" \
        || fail "desktop-file-validate rejects the override .desktop."
fi

log "== failure variants (invalid DEB-2 knobs) =="
if publish_fixture fail-priority -p:BundlerTestDebPriority=ultra \
        >"$integration_root/fail-priority.log" 2>&1; then
    fail "An invalid Priority must fail the publish."
fi
grep -qi "priority" "$integration_root/fail-priority.log" \
    || fail "The Priority failure did not mention Priority."
if publish_fixture fail-categories -p:BundlerTestDebCategories="Not A Category!" \
        >"$integration_root/fail-categories.log" 2>&1; then
    fail "Invalid Categories must fail the publish."
fi
grep -qi "categor" "$integration_root/fail-categories.log" \
    || fail "The Categories failure did not mention Categories."

log "== failure variant (relative install root) =="
if publish_fixture failure -p:BundlerTestDebInstallRoot="relative/path" \
        >"$integration_root/failure.log" 2>&1; then
    fail "A relative install root must fail the publish."
fi
grep -q "InstallRoot" "$integration_root/failure.log" || fail "The failure did not mention InstallRoot."
[[ ! -f "$integration_root/failure/linux-x64/deb/"*.deb ]] \
    || fail "A failed build left a .deb behind."

log "== API fixture (direct DebBundler via NuGet) =="
DEB_API_FIXTURE_OUTPUT="$api_output" \
    dotnet run --project "$api_fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    --packages "$package_cache" >/dev/null
api_deb="$api_output/artifacts/linux-x64/deb/api-fixture_1.0.0-1_amd64.deb"
[[ -f "$api_deb" ]] || fail "The direct-API fixture produced no .deb."
dpkg-deb -I "$api_deb" >/dev/null || fail "dpkg-deb rejects the API-fixture .deb."

log "== real install/remove smoke (sudo dpkg -i/-r) =="
if sudo -n true 2>/dev/null; then
    sudo -n dpkg -i "$deb_path" >/dev/null || fail "dpkg -i failed."
    status="$(dpkg -s bundler-deb-fixture)" || fail "dpkg -s failed after install."
    printf '%s' "$status" | grep -q "Status: install ok installed" \
        || fail "Package not registered as installed."
    printf '%s' "$status" | grep -qF "Version: 1.0.0-1" \
        || fail "dpkg reports the wrong installed version."
    [[ -x /usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture ]] \
        || fail "Installed executable missing/not executable."
    [[ -L /usr/bin/bundler-deb-fixture ]] || fail "usr/bin symlink not installed."
    [[ "$(/usr/bin/bundler-deb-fixture)" == BundlerDebIntegrationFixture:* ]] \
        || fail "The installed symlink did not launch the app."
    [[ "$(dpkg-deb -f "$deb_path" Installed-Size)" =~ ^[0-9]+$ ]] \
        || fail "Installed-Size is not numeric."
    log "== installed desktop integration (dpkg -L + readback) =="
    installed_files="$(dpkg -L bundler-deb-fixture)" || fail "dpkg -L failed."
    for path in "/usr/share/applications/bundler-deb-fixture.desktop" \
        "/usr/share/icons/hicolor/48x48/apps/bundler-deb-fixture.png" \
        "/usr/share/icons/hicolor/48x48@2/apps/bundler-deb-fixture.png" \
        "/usr/share/metainfo/bundler-deb-fixture.metainfo.xml" \
        "/usr/share/doc/bundler-deb-fixture/copyright" \
        "/usr/share/doc/bundler-deb-fixture/changelog.gz" \
        "/etc/bundler-deb-fixture/defaults.conf"; do
        printf '%s\n' "$installed_files" | grep -qxF "$path" \
            || fail "dpkg -L lacks '$path'."
        [[ -e "$path" ]] || fail "Installed path missing: $path"
    done
    installed_desktop="$(cat /usr/share/applications/bundler-deb-fixture.desktop)"
    printf '%s\n' "$installed_desktop" | grep -qx "Exec=bundler-deb-fixture %u" \
        || fail "The installed .desktop differs from the generated one."
    if command -v desktop-file-validate >/dev/null; then
        desktop-file-validate /usr/share/applications/bundler-deb-fixture.desktop \
            || fail "desktop-file-validate rejects the installed .desktop."
    fi
    # conffile 语义：/etc DebFile 自动登记 conffile → -r 保留 → -P 清除。
    status_conf="$(dpkg-query -W -f='${Conffiles}' bundler-deb-fixture)"         || fail "dpkg-query Conffiles failed."
    printf '%s' "$status_conf" | grep -q "etc/bundler-deb-fixture/defaults.conf" \
        || fail "The /etc file is not registered as a conffile: $status_conf"
    echo "local_edit=1" | sudo -n tee /etc/bundler-deb-fixture/defaults.conf >/dev/null
    sudo -n dpkg -r bundler-deb-fixture >/dev/null || fail "dpkg -r failed."
    [[ ! -e /usr/lib/bundler-deb-fixture && ! -e /usr/bin/bundler-deb-fixture \
        && ! -e /usr/share/applications/bundler-deb-fixture.desktop ]] \
        || fail "Removal left payload residue."
    grep -qx "local_edit=1" /etc/bundler-deb-fixture/defaults.conf \
        || fail "dpkg -r must keep the modified conffile."
    sudo -n dpkg -P bundler-deb-fixture >/dev/null || fail "dpkg -P failed."
    [[ ! -e /etc/bundler-deb-fixture ]] \
        || fail "dpkg -P did not purge the conffile."
    if dpkg -s bundler-deb-fixture >/dev/null 2>&1; then
        fail "dpkg -s still reports the package after purge."
    fi
    log "real dpkg install/remove verified (conffile -r keep / -P purge)"
else
    log "SKIP: passwordless sudo unavailable; real dpkg -i/-r not exercised."
fi

log "== scripts variant (maintainer scripts) =="
publish_fixture scripts \
    -p:BundlerTestDebPostinstFile="$script_dir/Fixture/Assets/postinst.sh" \
    -p:BundlerTestDebPrermFile="$script_dir/Fixture/Assets/prerm.sh" \
    -p:BundlerTestDebPostrmFile="$script_dir/Fixture/Assets/postrm.sh" >/dev/null
scripts_deb="$integration_root/scripts/linux-x64/deb/bundler-deb-fixture_1.0.0-1_amd64.deb"
[[ -f "$scripts_deb" ]] || fail "Scripts variant produced no .deb."
deb_control "$scripts_deb" scripts
for member in postinst prerm postrm; do
    [[ -f "$extract_root/control/scripts/$member" ]] \
        || fail "control archive lacks $member."
    [[ "$(stat -c %a "$extract_root/control/scripts/$member")" == "755" ]] \
        || fail "$member is not mode 0755."
    head -c2 "$extract_root/control/scripts/$member" | grep -qx '#!' \
        || fail "$member lacks a shebang."
done
[[ ! -f "$extract_root/control/scripts/preinst" ]] || fail "Unset preinst must not be packed."
[[ -f "$extract_root/control/scripts/conffiles" ]] \
    || fail "conffiles member missing in scripts variant."
grep -qx "/etc/bundler-deb-fixture/defaults.conf" "$extract_root/control/scripts/conffiles" \
    || fail "conffiles lacks the /etc DebFile destination."
if sudo -n true 2>/dev/null; then
    sudo -n rm -f /var/lib/bundler-deb-fixture-postinst.ran \
        /tmp/bundler-deb-fixture-postinst.ran /tmp/bundler-deb-fixture-prerm.ran \
        /tmp/bundler-deb-fixture-postrm.ran
    sudo -n dpkg -i "$scripts_deb" >/dev/null || fail "dpkg -i (scripts) failed."
    [[ -f /var/lib/bundler-deb-fixture-postinst.ran || -f /tmp/bundler-deb-fixture-postinst.ran ]] \
        || fail "postinst did not run (no marker file)."
    sudo -n dpkg -r bundler-deb-fixture >/dev/null || fail "dpkg -r (scripts) failed."
    [[ -f /tmp/bundler-deb-fixture-prerm.ran ]] || fail "prerm did not run."
    [[ -f /tmp/bundler-deb-fixture-postrm.ran ]] || fail "postrm did not run."
    sudo -n dpkg -P bundler-deb-fixture >/dev/null 2>&1 || true
    sudo -n rm -f /var/lib/bundler-deb-fixture-postinst.ran \
        /tmp/bundler-deb-fixture-postinst.ran /tmp/bundler-deb-fixture-prerm.ran \
        /tmp/bundler-deb-fixture-postrm.ran
    sudo -n rm -rf /etc/bundler-deb-fixture
    log "maintainer scripts executed under real dpkg -i/-r"
fi

log "== systemd unit variant (daemon-reload synthesis) =="
publish_fixture systemd \
    -p:BundlerTestDebSystemdServiceFile="$script_dir/Fixture/Assets/fixture.service" \
    -p:BundlerTestDebPostinstFile="$script_dir/Fixture/Assets/postinst.sh" >/dev/null
systemd_deb="$integration_root/systemd/linux-x64/deb/bundler-deb-fixture_1.0.0-1_amd64.deb"
deb_control "$systemd_deb" systemd
deb_data "$systemd_deb" systemd
[[ -f "$extract_root/data/systemd/usr/lib/systemd/system/bundler-deb-fixture.service" ]] \
    || fail "The systemd unit is missing from the payload."
grep -qx "ExecStart=/usr/bin/bundler-deb-fixture" \
    "$extract_root/data/systemd/usr/lib/systemd/system/bundler-deb-fixture.service" \
    || fail "The unit content differs."
postinst_text="$(cat "$extract_root/control/systemd/postinst")"
printf '%s\n' "$postinst_text" | grep -qx "systemctl daemon-reload || true" \
    || fail "postinst lacks the daemon-reload epilogue: $(printf '%s\n' "$postinst_text")"
printf '%s\n' "$postinst_text" | grep -q "bundler-deb-fixture-postinst.ran" \
    || fail "postinst lost the caller script body."

log "== upgrade variant (same package, newer version) =="
publish_fixture upgrade -p:BundlerVersion="1.0.1" >/dev/null
upgrade_deb="$integration_root/upgrade/linux-x64/deb/bundler-deb-fixture_1.0.1-1_amd64.deb"
[[ -f "$upgrade_deb" ]] || fail "Upgrade variant produced no .deb."
if sudo -n true 2>/dev/null; then
    sudo -n dpkg -i "$deb_path" >/dev/null || fail "dpkg -i v1.0.0 failed."
    echo "user_custom=42" | sudo -n tee /etc/bundler-deb-fixture/defaults.conf >/dev/null
    sudo -n dpkg -i --force-confold "$upgrade_deb" >/dev/null \
        || fail "Upgrade dpkg -i v1.0.1 failed."
    status="$(dpkg -s bundler-deb-fixture)" || fail "dpkg -s failed after upgrade."
    printf '%s' "$status" | grep -qF "Version: 1.0.1-1" \
        || fail "dpkg reports the wrong version after upgrade."
    grep -qx "user_custom=42" /etc/bundler-deb-fixture/defaults.conf \
        || fail "Upgrade overwrote the locally modified conffile."
    sudo -n dpkg -P bundler-deb-fixture >/dev/null || fail "dpkg -P after upgrade failed."
    [[ ! -e /etc/bundler-deb-fixture ]] || fail "Purge left the conffile."
    log "upgrade + conffile preservation verified"
fi

log "== failure variant (unsupported compression) =="
if publish_fixture fail-compression -p:BundlerTestDebCompression=xz \
        >"$integration_root/fail-compression.log" 2>&1; then
    fail "An unsupported compression must fail the publish."
fi
grep -qi "compression" "$integration_root/fail-compression.log" \
    || fail "The compression failure did not mention compression."

log "== arm64 variant (cross-arch structure; install is OI-01) =="
publish_fixture arm64 -r linux-arm64 >"$integration_root/arm64.log" 2>&1 \
    || { cat "$integration_root/arm64.log"; fail "linux-arm64 publish failed."; }
arm64_deb="$(find "$integration_root/arm64" -name "*_arm64.deb" | head -1)"
[[ -n "$arm64_deb" ]] || fail "No *_arm64.deb produced for linux-arm64."
arm64_info="$(dpkg-deb -I "$arm64_deb")" || fail "dpkg-deb -I rejects the arm64 package."
printf '%s' "$arm64_info" | grep -q "Architecture: arm64" \
    || fail "arm64 package lacks Architecture: arm64: $(printf '%s' "$arm64_info")"
arm64_listing="$(dpkg-deb -c "$arm64_deb")" || fail "dpkg-deb -c rejects the arm64 package."
printf '%s' "$arm64_listing" \
    | grep -q "usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture" \
    || fail "arm64 payload lacks the usr/lib payload."
log "arm64 package structure verified (host install not possible on amd64)."

if command -v lintian >/dev/null; then
    log "== lintian gate (exemption list enforced) =="
    lintian_out="$(lintian "$deb_path" || true)"
    printf '%s\n' "$lintian_out" | sed -n 's/^[EW]: [^:]*: \([^ ]*\).*/\1/p' \
        | sort -u >"$integration_root/lintian-tags.txt"
    unexpected="$(comm -23 "$integration_root/lintian-tags.txt" <(sort -u "$script_dir/lintian-exemptions.txt") || true)"
    if [[ -n "$unexpected" ]]; then
        printf '%s\n' "$lintian_out" >&2
        fail "lintian reported non-exempt tags: $unexpected"
    fi
    printf '%s\n' "$lintian_out" | sed 's/^/lintian: /'
else
    log "SKIP: lintian unavailable on this host."
fi

if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
    for image in debian:stable ubuntu:latest; do
        log "== docker matrix: $image =="
        if ! docker run --rm -v "$deb_path:/tmp/pkg.deb:ro" "$image" sh -c '
            set -e
            dpkg -i /tmp/pkg.deb >/dev/null
            dpkg -s bundler-deb-fixture | grep -q "Status: install ok installed"
            bundler-deb-fixture smoke | grep -q "BundlerDebIntegrationFixture:smoke"
            test -f /etc/bundler-deb-fixture/defaults.conf
            dpkg -r bundler-deb-fixture >/dev/null
            test -f /etc/bundler-deb-fixture/defaults.conf   # conffile survives -r
            ! dpkg -s bundler-deb-fixture >/dev/null 2>&1
            dpkg -P bundler-deb-fixture >/dev/null 2>&1 || true  # purge leftover conffile
            test ! -e /etc/bundler-deb-fixture/defaults.conf
        '; then
            fail "docker $image install/run/remove failed."
        fi
    done
    log "docker matrix green on debian:stable + ubuntu:latest."
else
    log "SKIP: docker unavailable — container matrix not run."
fi

log "PASS: LINUX-DEB-1..4 integration checks complete."
