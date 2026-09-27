#!/usr/bin/env bash
# LINUX-DEB-1 .deb 集成验证：真实 .NET payload → BundlerFormats=deb → ar/dpkg-deb 结构断言
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
for tool in dotnet ar tar md5sum sha256sum dpkg-deb unzip; do
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
    "Maintainer: DotNet.Bundler Tests" "Priority: optional" \
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

deb_data "$deb_path" default
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
    sudo -n dpkg -r bundler-deb-fixture >/dev/null || fail "dpkg -r failed."
    [[ ! -e /usr/lib/bundler-deb-fixture && ! -e /usr/bin/bundler-deb-fixture ]] \
        || fail "Removal left payload residue."
    if dpkg -s bundler-deb-fixture >/dev/null 2>&1; then
        fail "dpkg -s still reports the package after removal."
    fi
    log "real dpkg install/remove verified"
else
    log "SKIP: passwordless sudo unavailable; real dpkg -i/-r not exercised."
fi

log "PASS: LINUX-DEB-1 integration checks complete."
