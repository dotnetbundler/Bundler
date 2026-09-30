#!/usr/bin/env bash
# ARCHIVE-1 .zip/.tar.gz 集成验证：真实 .NET payload → BundlerFormats=zip;targz
# → 命名/.sha256 侧车断言 → unzip -l/zipinfo -l 与 tar -tvf 清单+mode 断言
# → 真实解包逐路径断言 → 解出载荷运行 + mode/symlink 还原断言 → 覆盖变体
# → BundlerArchiveFile 映射与非法目标失败变体 → deb;rpm;appimage;zip;targz 扇出。
# 用法: bash tests/Archive.Integration/Verify.sh
# 需要 Linux 宿主与 dotnet SDK；产物仅在 artifacts/archive-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/archive-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerArchiveIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/Archive.Api.PackageFixture/Archive.Api.PackageFixture.csproj"
package_cache="$integration_root/nuget-cache"
extract_root="$integration_root/extract"
identity="BundlerArchiveIntegration"
stem="bundler-archive-fixture-1.0.0-linux-x64"
exe="BundlerArchiveIntegrationFixture"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

if [[ "$(uname -s)" != "Linux" ]]; then log "integration test skipped: non-Linux host"; exit 0; fi
for tool in dotnet sha256sum unzip zipinfo tar; do
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
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.Archive; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
# MSBuild 包内必须装载 Archive 后端任务程序集。
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.Archive.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the Archive backend assembly."

publish_fixture() {
    # $1: 输出子目录；其余参数透传为 -p:BundlerTest* 覆盖。
    local name="$1"; shift
    dotnet publish "$fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$integration_root/$name" \
        --packages "$package_cache" "$@"
}

assert_zip() {
    # $1: .zip 路径, $2: stem
    local zip="$1" s="$2"
    [[ -f "$zip.sha256" ]] || fail "$zip: missing sha256 sidecar."
    (cd "$(dirname "$zip")" && sha256sum -c "$(basename "$zip").sha256") \
        || fail "$zip: sha256 sidecar mismatch."
    unzip -l "$zip" > "$integration_root/zip.list" || fail "unzip -l failed on $zip"
    grep -q " $s/$" "$integration_root/zip.list" \
        || fail "$zip: top-level directory entry missing."
    grep -q " $s/$exe$" "$integration_root/zip.list" \
        || fail "$zip: payload executable entry missing."
    # zipinfo -l 逐条目断言 unix mode：主程序 0755、普通文件 0644、目录 0755、链接 0777。
    zipinfo -l "$zip" > "$integration_root/zip.modes" || fail "zipinfo -l failed on $zip"
    grep -qE "^-rwxr-xr-x .* $s/$exe$" "$integration_root/zip.modes" \
        || fail "$zip: $exe does not carry mode 0755 (got: $(grep " $s/$exe\$" "$integration_root/zip.modes"))"
    grep -qE "^-rw-r--r-- .* $s/$exe.dll$" "$integration_root/zip.modes" \
        || fail "$zip: $exe.dll does not carry mode 0644 (got: $(grep " $s/$exe.dll\$" "$integration_root/zip.modes"))"
    grep -qE "^drwxr-xr-x .* $s/$" "$integration_root/zip.modes" \
        || fail "$zip: top-level directory does not carry mode 0755."
    grep -qE "^lrwxrwxrwx .* $s/$exe.link$" "$integration_root/zip.modes" \
        || fail "$zip: $exe.link does not carry symlink mode 0777 (got: $(grep " $s/$exe.link\$" "$integration_root/zip.modes"))"
}

assert_targz() {
    # $1: .tar.gz 路径, $2: stem
    local tgz="$1" s="$2"
    [[ -f "$tgz.sha256" ]] || fail "$tgz: missing sha256 sidecar."
    (cd "$(dirname "$tgz")" && sha256sum -c "$(basename "$tgz").sha256") \
        || fail "$tgz: sha256 sidecar mismatch."
    tar -tzvf "$tgz" > "$integration_root/tar.list" || fail "tar -tzvf failed on $tgz"
    grep -qE "^drwxr-xr-x .* $s/$" "$integration_root/tar.list" \
        || fail "$tgz: top-level directory entry missing or wrong mode."
    grep -qE "^-rwxr-xr-x .* $s/$exe$" "$integration_root/tar.list" \
        || fail "$tgz: $exe entry missing or wrong mode."
    grep -qE "^-rw-r--r-- .* $s/$exe.dll$" "$integration_root/tar.list" \
        || fail "$tgz: $exe.dll entry missing or wrong mode."
    grep -qE "^lrwxrwxrwx .* $s/$exe.link -> \./$exe$" "$integration_root/tar.list" \
        || fail "$tgz: symlink entry missing or target mismatch (got: $(grep "$exe.link" "$integration_root/tar.list"))"
}

assert_extracted_runs() {
    # $1: 解出根, $2: stem
    local root="$1" s="$2"
    [[ -d "$root/$s" ]] || fail "extracted top-level directory $s missing."
    [[ -x "$root/$s/$exe" ]] || fail "extracted payload $exe is not executable."
    [[ -f "$root/$s/$exe.dll" ]] || fail "extracted payload $exe.dll missing."
    [[ -L "$root/$s/$exe.link" ]] || fail "extracted $exe.link is not a symlink."
    [[ "$(readlink "$root/$s/$exe.link")" == "./$exe" ]] \
        || fail "extracted symlink target mismatch: $(readlink "$root/$s/$exe.link")"
    local out
    out="$("$root/$s/$exe" probe 2>/dev/null)" \
        || fail "extracted payload failed to run."
    [[ "$out" == "BundlerArchiveIntegrationFixture:probe" ]] \
        || fail "extracted payload output mismatch: $out"
}

log "== publishing the fixture (BundlerFormats=zip;targz) =="
publish_fixture default >/dev/null
zip="$(find "$integration_root/default/linux-x64/zip" -name '*.zip' | head -n1)"
tgz="$(find "$integration_root/default/linux-x64/targz" -name '*.tar.gz' | head -n1)"
[[ -n "$zip" ]] || fail "No .zip artifact under the default publish output."
[[ -n "$tgz" ]] || fail "No .tar.gz artifact under the default publish output."
[[ "$(basename "$zip")" == "$stem.zip" ]] || fail "Unexpected zip name: $(basename "$zip")"
[[ "$(basename "$tgz")" == "$stem.tar.gz" ]] || fail "Unexpected tar.gz name: $(basename "$tgz")"

log "== zip listing + mode assertions (unzip -l / zipinfo -l) =="
assert_zip "$zip" "$stem"

log "== tar.gz listing + mode assertions (tar -tvf) =="
assert_targz "$tgz" "$stem"

log "== real extraction: unzip payload runs, exec bit + symlink restored =="
rm -rf "$extract_root/zip" && mkdir -p "$extract_root/zip"
unzip -q "$zip" -d "$extract_root/zip" || fail "unzip extraction failed."
assert_extracted_runs "$extract_root/zip" "$stem"

log "== cross-implementation reads: python3 zipfile/tarfile =="
python3 - "$zip" "$tgz" "$stem" "$exe" <<'PYEOF'
import sys, tarfile, zipfile
zip_path, tgz_path, stem, exe = sys.argv[1:5]
z = zipfile.ZipFile(zip_path)
names = z.namelist()
assert f"{stem}/{exe}" in names, "zipfile: payload entry missing"
info = z.getinfo(f"{stem}/{exe}")
assert (info.external_attr >> 16) & 0o111 != 0, "zipfile: exec bits not visible"
assert z.read(f"{stem}/{exe}.link") == b"./" + exe.encode(), "zipfile: symlink content mismatch"
t = tarfile.open(tgz_path, "r:gz")
t_names = t.getnames()
assert f"{stem}/{exe}" in t_names, "tarfile: payload entry missing"
sym = t.getmember(f"{stem}/{exe}.link")
assert sym.issym() and sym.linkname == f"./{exe}", "tarfile: symlink member mismatch"
PYEOF

log "== real extraction: tar -xzf payload runs, exec bit + symlink restored =="
rm -rf "$extract_root/targz" && mkdir -p "$extract_root/targz"
tar -xzf "$tgz" -C "$extract_root/targz" || fail "tar -xzf extraction failed."
assert_extracted_runs "$extract_root/targz" "$stem"

log "== override variant: BundlerArchivePackageName/Version/ArchiveName =="
publish_fixture override -p:BundlerTestArchivePackageName=acme-tool \
    -p:BundlerTestArchiveVersion=2.3.4 \
    -p:BundlerTestFormats=zip >/dev/null
override_zip="$(find "$integration_root/override/linux-x64/zip" -name '*.zip' | head -n1)"
[[ -n "$override_zip" ]] || fail "override: no zip produced."
[[ "$(basename "$override_zip")" == "acme-tool-2.3.4-linux-x64.zip" ]] \
    || fail "override: expected stem acme-tool-2.3.4-linux-x64, got $(basename "$override_zip")"
unzip -l "$override_zip" > "$integration_root/override.list" || fail "override: unzip -l failed."
grep -q " acme-tool-2.3.4-linux-x64/$exe$" "$integration_root/override.list" \
    || fail "override: stem directory mismatch inside archive."

publish_fixture named -p:BundlerTestArchiveName=custom-stem \
    -p:BundlerTestFormats=zip >/dev/null
named_zip="$(find "$integration_root/named/linux-x64/zip" -name 'custom-stem.zip' | head -n1)"
[[ -n "$named_zip" ]] || fail "ArchiveName override did not produce custom-stem.zip."
unzip -l "$named_zip" > "$integration_root/named.list" || fail "named: unzip -l failed."
grep -q " custom-stem/$exe$" "$integration_root/named.list" \
    || fail "ArchiveName override: entries must sit under custom-stem/."

log "== BundlerArchiveFile mapping lands under the top-level dir =="
publish_fixture files -p:BundlerTestArchiveFiles=1 -p:BundlerTestFormats='zip%3Btargz' >/dev/null
files_zip="$(find "$integration_root/files/linux-x64/zip" -name '*.zip' | head -n1)"
files_tgz="$(find "$integration_root/files/linux-x64/targz" -name '*.tar.gz' | head -n1)"
unzip -l "$files_zip" > "$integration_root/files-zip.list" || fail "files: unzip -l failed."
grep -q " $stem/extras/defaults.conf$" "$integration_root/files-zip.list" \
    || fail "files(zip): mapped file missing."
tar -tzvf "$files_tgz" > "$integration_root/files-tgz.list" || fail "files: tar -tzvf failed."
grep -qE "^-rw-r--r-- .* $stem/extras/defaults.conf$" "$integration_root/files-tgz.list" \
    || fail "files(targz): mapped file missing."
rm -rf "$extract_root/files" && mkdir -p "$extract_root/files"
unzip -q "$files_zip" -d "$extract_root/files"
grep -qx "fixture=defaults" "$extract_root/files/$stem/extras/defaults.conf" \
    || fail "files(zip): mapped file content mismatch."

log "== failure variant: absolute BundlerArchiveFile destination must fail =="
if dotnet publish "$fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    -p:BundlerIntegrationOutput="$integration_root/badfile" \
    -p:BundlerTestArchiveBadFile=1 \
    --packages "$package_cache" > "$integration_root/badfile.log" 2>&1; then
    fail "publish with an absolute BundlerArchiveFile destination unexpectedly succeeded."
fi
grep -qi "BundlerArchiveFile destination" "$integration_root/badfile.log" \
    || fail "bad-file failure did not report the destination validation error."
[[ -z "$(find "$integration_root/badfile" -name '*.zip' -o -name '*.tar.gz' 2>/dev/null)" ]] \
    || fail "bad-file failure left partial artifacts behind."

log "== multi-format fanout: deb;rpm;appimage;zip;targz in one publish =="
rm -rf "$package_cache"
publish_fixture fanout -p:BundlerTestFormats="deb%3Brpm%3Bappimage%3Bzip%3Btargz" >/dev/null
fanout_dir="$integration_root/fanout/linux-x64"
[[ -n "$(find "$fanout_dir" -name '*.deb' -print -quit)" ]] || fail "fanout: .deb missing."
[[ -n "$(find "$fanout_dir" -name '*.rpm' -print -quit)" ]] || fail "fanout: .rpm missing."
[[ -n "$(find "$fanout_dir" -name '*.AppImage' -print -quit)" ]] || fail "fanout: .AppImage missing."
[[ -n "$(find "$fanout_dir" -name '*.zip' -print -quit)" ]] || fail "fanout: .zip missing."
[[ -n "$(find "$fanout_dir" -name '*.tar.gz' -print -quit)" ]] || fail "fanout: .tar.gz missing."

log "== cross-OS matrix: windows/macOS targets accept zip =="
for rid in win-x64 osx-arm64; do
    out_dir="$integration_root/cross-$rid"
    dotnet publish "$fixture_project" -c Release -r "$rid" \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$out_dir" \
        -p:BundlerTestFormats=zip \
        --packages "$package_cache" >/dev/null \
        || fail "cross publish for $rid failed."
    [[ -n "$(find "$out_dir/$rid/zip" -name '*.zip' -print -quit)" ]] \
        || fail "cross: no zip produced for $rid."
done

log "== direct API consumption via DotNet.Bundler.Archive nupkg =="
ARCHIVE_API_FIXTURE_OUTPUT="$integration_root/api" \
    dotnet run --project "$api_fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        --packages "$package_cache" | tee "$integration_root/api.log"
grep -q "OK: " "$integration_root/api.log" || fail "API fixture did not produce archives."

log "== all archive integration assertions passed =="
