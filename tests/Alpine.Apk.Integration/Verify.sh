#!/usr/bin/env bash
# APK-4 .apk 集成验证：真实 .NET payload → BundlerFormats=alpineapk → gzip 多段
# 结构断言（python3 zlib 逐成员拆分）→ .PKGINFO/datahash/pax 校验 → 三次产出
# 逐字节一致 → alpine:latest 容器内 apk add/--allow-untrusted 实装+运行+移除、
# 签名包（公钥落 /etc/apk/keys 免 --allow-untrusted）与 aarch64 binfmt 腿。
# 用法: bash tests/Alpine.Apk.Integration/Verify.sh
# 需要 Linux 宿主与 dotnet SDK；产物仅在 artifacts/alpine-apk-integration 下落盘并全部清理。
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
integration_root="$repo_root/artifacts/alpine-apk-integration"
package_dir="$repo_root/artifacts/packages"
fixture_project="$script_dir/Fixture/BundlerAlpineApkIntegrationFixture.csproj"
api_fixture_project="$repo_root/tests/AlpineApk.Api.PackageFixture/AlpineApk.Api.PackageFixture.csproj"
api_output="$integration_root/api"
package_cache="$integration_root/nuget-cache"
extract_root="$integration_root/extract"
identity="BundlerAlpineApkIntegration"

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -f "$integration_root/.bundler-identity" ]] && grep -qx "$identity" "$integration_root/.bundler-identity" 2>/dev/null; then
        rm -rf "$integration_root"
    fi
}
trap cleanup EXIT

if [[ "$(uname -s)" != "Linux" ]]; then log "integration test skipped: non-Linux host"; exit 0; fi
for tool in dotnet tar sha256sum gzip openssl python3 unzip; do
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
mkdir -p "$integration_root" "$extract_root"
printf '%s\n' "$identity" > "$integration_root/.bundler-identity"

log "== building repository packages =="
dotnet build "$repo_root/Bundler.slnx" -c Release >/dev/null
dotnet pack "$repo_root/Bundler.slnx" -c Release -o "$package_dir" >/dev/null
for package_id in DotNet.Bundler DotNet.Bundler.MSBuild DotNet.Bundler.AlpineApk; do
    [[ -f "$package_dir/$package_id.$version.nupkg" ]] || fail "Missing package $package_id.$version.nupkg"
done
unzip -l "$package_dir/DotNet.Bundler.MSBuild.$version.nupkg" > "$integration_root/msbuild-package.list" \
    || fail "Cannot list the DotNet.Bundler.MSBuild package."
grep -q "DotNet.Bundler.AlpineApk.dll" "$integration_root/msbuild-package.list" \
    || fail "DotNet.Bundler.MSBuild package is missing the AlpineApk backend assembly."

publish_fixture() {
    # $1: 输出子目录；其余参数透传为 -p:BundlerTestAlpineApk* / -r 等覆盖。
    local name="$1"; shift
    dotnet publish "$fixture_project" -c Release \
        -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
        -p:BundlerIntegrationOutput="$integration_root/$name" \
        --packages "$package_cache" "$@"
}

split_apk() {
    # $1: .apk 路径；$2: 输出目录。逐成员写 memberN.gz/memberN.tar 并回显成员数。
    # GZipStream 会把拼接成员读成一条流，必须按 unused_data 逐成员切分。
    local dest="$2"
    rm -rf "$dest" && mkdir -p "$dest"
    python3 - "$1" "$dest" <<'PY'
import sys, zlib
data = open(sys.argv[1], "rb").read()
dest = sys.argv[2]
offset = 0
count = 0
while offset < len(data):
    d = zlib.decompressobj(31)
    payload = d.decompress(data[offset:])
    consumed = len(data) - offset - len(d.unused_data)
    if consumed <= 0:
        break
    open(f"{dest}/member{count}.gz", "wb").write(data[offset:offset + consumed])
    open(f"{dest}/member{count}.tar", "wb").write(payload)
    offset += consumed
    count += 1
print(count)
PY
}

nonzero_tail() {
    # $1: tar 路径；末 1024 字节存在非零内容时返回 0。
    [[ "$(tail -c 1024 "$1" | tr -d '\000' | wc -c)" -gt 0 ]]
}

log "== publishing fixture (BundlerFormats=alpineapk) =="
publish_fixture bundle >/dev/null

apk_path="$integration_root/bundle/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk"
[[ -f "$apk_path" ]] || fail "The .apk artifact is missing: $(find "$integration_root/bundle" -type f)"
[[ -f "$apk_path.sha256" ]] || fail "The .sha256 sidecar is missing."
log "produced: $apk_path"

log "== sha256 sidecar =="
(cd "$(dirname "$apk_path")" && sha256sum -c "$(basename "$apk_path").sha256") >/dev/null \
    || fail "The .sha256 sidecar does not match the .apk."

log "== gzip member structure =="
members="$(split_apk "$apk_path" "$extract_root/default")"
[[ "$members" == "2" ]] || fail "An unsigned .apk must hold 2 gzip members, got $members."
nonzero_tail "$extract_root/default/member0.tar" \
    || fail "The control tar must not end with zero blocks."
[[ "$(tail -c 1024 "$extract_root/default/member1.tar" | tr -d '\000' | wc -c)" == "0" ]] \
    || fail "The data tar must end with zero blocks."

log "== .PKGINFO fields =="
pkginfo="$(tar -xOf "$extract_root/default/member0.tar" .PKGINFO)" \
    || fail "The control member lacks .PKGINFO."
for field in "pkgname = bundler-apk-fixture" "pkgver = 1.0.0-r0" \
    "pkgdesc = Disposable .apk integration-test fixture." \
    "url = https://example.com/apk-fixture" "arch = x86_64" \
    "origin = bundler-apk-fixture" "builddate = 0" \
    "depend = libstdc++" "depend = libgcc"; do
    printf '%s\n' "$pkginfo" | grep -qxF "$field" \
        || fail ".PKGINFO lacks '$field': $(printf '%s\n' "$pkginfo")"
done
datahash="$(printf '%s\n' "$pkginfo" | sed -n 's/^datahash = //p')"
[[ "$datahash" == "$(sha256sum "$extract_root/default/member1.gz" | cut -d' ' -f1)" ]] \
    || fail "datahash must be the sha256 of the data gzip stream: $datahash"
reported_size="$(printf '%s\n' "$pkginfo" | sed -n 's/^size = //p')"
actual_size="$(tar -tvf "$extract_root/default/member1.tar" | awk '$1 ~ /^-/ { s += $3 } END { print s }')"
[[ "$reported_size" == "$actual_size" ]] \
    || fail "size must equal the payload file byte sum: $reported_size != $actual_size"
tar -tf "$extract_root/default/member0.tar" | grep -q "^\.SIGN\." \
    && fail "An unsigned package must not contain .SIGN.* members." || true

log "== payload contents (data member) =="
listing="$(tar -tvf "$extract_root/default/member1.tar")" || fail "Cannot list the data member."
printf '%s\n' "$listing" | grep -q "usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture" \
    || fail "Payload lacks usr/lib payload: $(printf '%s\n' "$listing")"
printf '%s\n' "$listing" | grep -q "usr/lib/bundler-apk-fixture/docs/readme.txt" \
    || fail "Payload lacks the BundlerResource at docs/readme.txt."
printf '%s\n' "$listing" | grep -q "etc/bundler-apk-fixture/defaults.conf" \
    || fail "Payload lacks the BundlerAlpineApkFile /etc entry."
printf '%s\n' "$listing" | grep -q "usr/bin/bundler-apk-fixture -> ../lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture" \
    || fail "Payload lacks the relative usr/bin symlink: $(printf '%s\n' "$listing")"
printf '%s\n' "$listing" | grep -qE "^-rwxr-xr-x .*usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture$" \
    || fail "The main executable lacks mode 0755."
file_count="$(printf '%s\n' "$listing" | grep -c '^[-l]')"
pax_count="$(grep -a -o 'APK-TOOLS\.checksum\.SHA1' "$extract_root/default/member1.tar" | wc -l)"
[[ "$pax_count" == "$file_count" ]] \
    || fail "Every payload file and symlink needs an APK-TOOLS.checksum.SHA1 pax record ($pax_count != $file_count)."
pax_value="$(grep -a -m1 -o 'APK-TOOLS\.checksum\.SHA1=[0-9a-f]\{40\}' "$extract_root/default/member1.tar")"
[[ -n "$pax_value" ]] || fail "The checksum pax record must be a hex sha1 like abuild emits."

log "== override variant (name/release/license/relations) =="
publish_fixture override \
    -p:BundlerTestAlpineApkPackageName=custom-apk \
    -p:BundlerTestAlpineApkRelease=7 \
    -p:BundlerTestAlpineApkLicense=MIT \
    -p:BundlerTestAlpineApkDepends="musl%3Bso:libc.musl-x86_64.so.1>=1.2" \
    -p:BundlerTestAlpineApkProvides=virtual-apk-fixture \
    -p:BundlerTestAlpineApkTriggers="/usr/lib/bundler-apk-fixture" >/dev/null
override_apk="$integration_root/override/linux-musl-x64/apk/custom-apk-1.0.0-r7.apk"
[[ -f "$override_apk" ]] || fail "Override variant produced no .apk: $(find "$integration_root/override" -type f)"
split_apk "$override_apk" "$extract_root/override" >/dev/null
pkginfo="$(tar -xOf "$extract_root/override/member0.tar" .PKGINFO)"
for field in "pkgname = custom-apk" "pkgver = 1.0.0-r7" "license = MIT" \
    "depend = musl" "depend = so:libc.musl-x86_64.so.1>=1.2" \
    "provides = virtual-apk-fixture" "triggers = /usr/lib/bundler-apk-fixture"; do
    printf '%s\n' "$pkginfo" | grep -qxF "$field" \
        || fail "Override .PKGINFO lacks '$field': $(printf '%s\n' "$pkginfo")"
done

log "== scripts variant (.post-install/.pre-deinstall) =="
publish_fixture scripts \
    -p:BundlerTestAlpineApkPostInstallScript="$script_dir/Fixture/Assets/post-install.sh" \
    -p:BundlerTestAlpineApkPreDeinstallScript="$script_dir/Fixture/Assets/pre-deinstall.sh" >/dev/null
scripts_apk="$integration_root/scripts/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk"
[[ -f "$scripts_apk" ]] || fail "Scripts variant produced no .apk."
split_apk "$scripts_apk" "$extract_root/scripts" >/dev/null
control_listing="$(tar -tvf "$extract_root/scripts/member0.tar")"
for member in .post-install .pre-deinstall; do
    printf '%s\n' "$control_listing" | grep -qE "^-rwxr-xr-x .* ${member}$" \
        || fail "control member lacks $member at mode 0755: $(printf '%s\n' "$control_listing")"
done
[[ "$(tar -tf "$extract_root/scripts/member0.tar" | grep -c "^\.pre-install$")" == "0" ]] \
    || fail "Unset scripts must not be packed."

log "== signing variant (.SIGN.RSA member) =="
mkdir -p "$integration_root/keys"
openssl genrsa -out "$integration_root/keys/bundler-test.rsa" 2048 2>/dev/null \
    || fail "openssl genrsa failed."
openssl rsa -in "$integration_root/keys/bundler-test.rsa" \
    -pubout -out "$integration_root/keys/bundler-test.rsa.rsa.pub" 2>/dev/null \
    || fail "openssl pubkey export failed."
publish_fixture signed \
    -p:BundlerTestAlpineApkSigningKeyFile="$integration_root/keys/bundler-test.rsa" >/dev/null
signed_apk="$integration_root/signed/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk"
[[ -f "$signed_apk" ]] || fail "Signed variant produced no .apk."
members="$(split_apk "$signed_apk" "$extract_root/signed")"
[[ "$members" == "3" ]] || fail "A signed .apk must hold 3 gzip members, got $members."
nonzero_tail "$extract_root/signed/member0.tar" \
    || fail "The signature tar must not end with zero blocks."
[[ "$(tar -tf "$extract_root/signed/member0.tar")" == ".SIGN.RSA.bundler-test.rsa.rsa.pub" ]] \
    || fail "Signature member name mismatch: $(tar -tf "$extract_root/signed/member0.tar")"
tar -xOf "$extract_root/signed/member0.tar" .SIGN.RSA.bundler-test.rsa.rsa.pub \
    > "$extract_root/signed/sig.bin" || fail "Cannot extract the signature blob."
openssl dgst -sha1 -verify "$integration_root/keys/bundler-test.rsa.rsa.pub" \
    -signature "$extract_root/signed/sig.bin" "$extract_root/signed/member1.gz" \
    | grep -q "Verified OK" || fail "openssl cannot verify the .SIGN.RSA signature."

log "== determinism (three consecutive produces) =="
publish_fixture det1 >/dev/null
publish_fixture det2 >/dev/null
publish_fixture det3 >/dev/null
sha1="$(sha256sum "$integration_root/det1/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk" | cut -d' ' -f1)"
sha2="$(sha256sum "$integration_root/det2/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk" | cut -d' ' -f1)"
sha3="$(sha256sum "$integration_root/det3/linux-musl-x64/apk/bundler-apk-fixture-1.0.0-r0.apk" | cut -d' ' -f1)"
[[ "$sha1" == "$sha2" && "$sha2" == "$sha3" ]] \
    || fail "Three consecutive produces are not byte-identical: $sha1 $sha2 $sha3"

log "== API fixture (direct AlpineApkBundler via NuGet) =="
APK_API_FIXTURE_OUTPUT="$api_output" \
    dotnet run --project "$api_fixture_project" -c Release \
    -p:RestoreSources="$package_dir;https://api.nuget.org/v3/index.json" \
    --packages "$package_cache" >/dev/null
api_apk="$api_output/artifacts/linux-musl-x64/apk/api-fixture-1.0.0-r0.apk"
[[ -f "$api_apk" ]] || fail "The direct-API fixture produced no .apk."
[[ "$(od -An -tx1 -N3 "$api_apk" | tr -d ' ')" == "1f8b08" ]] \
    || fail "The API-fixture artifact is not a gzip stream."

log "== failure variant (invalid release) =="
if publish_fixture fail-release -p:BundlerTestAlpineApkRelease=abc \
        >"$integration_root/fail-release.log" 2>&1; then
    fail "A non-numeric Release must fail the publish."
fi
grep -qi "release\|Release" "$integration_root/fail-release.log" \
    || fail "The Release failure did not mention release."

if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
    log "== docker: alpine:latest real install/remove =="
    docker run --rm -v "$scripts_apk:/tmp/pkg.apk:ro" alpine:latest sh -c '
        set -e
        apk add --allow-untrusted /tmp/pkg.apk >/dev/null
        test -f /tmp/bundler-apk-fixture-post-install.ran
        test -f /etc/bundler-apk-fixture/defaults.conf
        bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
        test "$(readlink /usr/bin/bundler-apk-fixture)" = "../lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture"
        apk info -e bundler-apk-fixture
        apk del bundler-apk-fixture >/dev/null
        test -f /tmp/bundler-apk-fixture-pre-deinstall.ran
        ! test -e /usr/lib/bundler-apk-fixture
        ! test -e /usr/bin/bundler-apk-fixture
    ' || fail "alpine install/run/remove leg failed."

    log "== docker: unsigned package rejected without --allow-untrusted =="
    if docker run --rm -v "$apk_path:/tmp/pkg.apk:ro" alpine:latest \
            sh -c 'apk add /tmp/pkg.apk >/dev/null 2>&1'; then
        fail "An unsigned package must not install without --allow-untrusted."
    fi

    log "== docker: signed package installs via trusted key =="
    docker run --rm \
        -v "$signed_apk:/tmp/signed.apk:ro" \
        -v "$integration_root/keys/bundler-test.rsa.rsa.pub:/tmp/key.rsa.pub:ro" \
        alpine:latest sh -c '
            set -e
            cp /tmp/key.rsa.pub /etc/apk/keys/bundler-test.rsa.rsa.pub
            apk add /tmp/signed.apk >/dev/null
            bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
            apk del bundler-apk-fixture >/dev/null
        ' || fail "The signed .apk did not install as a trusted package."

    log "== arm64 variant =="
    publish_fixture arm64 -r linux-musl-arm64 >"$integration_root/arm64.log" 2>&1 \
        || { cat "$integration_root/arm64.log"; fail "linux-musl-arm64 publish failed."; }
    arm64_apk="$integration_root/arm64/linux-musl-arm64/apk/bundler-apk-fixture-1.0.0-r0.apk"
    [[ -f "$arm64_apk" ]] || fail "No .apk produced for linux-musl-arm64."
    split_apk "$arm64_apk" "$extract_root/arm64" >/dev/null
    tar -xOf "$extract_root/arm64/member0.tar" .PKGINFO | grep -qx "arch = aarch64" \
        || fail "arm64 package lacks arch = aarch64."
    arm64_kernel="$(docker run --rm --platform linux/arm64 alpine:latest uname -m 2>/dev/null || true)"
    if [[ "$arm64_kernel" != "aarch64" ]]; then
        docker run --rm --privileged tonistiigi/binfmt --install aarch64 >/dev/null 2>&1 || true
        arm64_kernel="$(docker run --rm --platform linux/arm64 alpine:latest uname -m 2>/dev/null || true)"
    fi
    if [[ "$arm64_kernel" == "aarch64" ]]; then
        docker run --rm --platform linux/arm64 \
            -v "$arm64_apk:/tmp/pkg.apk:ro" alpine:latest sh -c '
                set -e
                apk add --allow-untrusted /tmp/pkg.apk >/dev/null
                bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
                apk del bundler-apk-fixture >/dev/null
            ' || fail "aarch64 container install/run/remove failed."
        log "aarch64 install verified under binfmt qemu."
    else
        log "SKIP: aarch64 emulation unavailable; arm64 leg is structure-only."
    fi
else
    log "SKIP: docker unavailable — container legs not run."
fi

log "PASS: APK-4 integration checks complete."
