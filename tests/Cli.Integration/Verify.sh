#!/usr/bin/env bash
# CLI-C1 真实验证：bundler validate/plan/bundle 对真实 publish 目录跑通五种 Linux 可产格式；
# 断言退出码分级（0/1/2）、--json 机器可读输出、三档日志与 'all' 展开。
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARTIFACTS="$REPO_ROOT/artifacts/cli-integration"
FIXTURE_DIR="$REPO_ROOT/tests/Cli.Integration/Fixture"
CLI_DLL="$REPO_ROOT/src/Bundler.Cli/bin/Release/net10.0/bundler.dll"
PUBLISH_DIR="$ARTIFACTS/publish"
OUT_DIR="$ARTIFACTS/out"

die() { echo "ASSERTION FAILED: $*" >&2; exit 1; }
log() { echo "== $* =="; }

for arg in "$@"; do
  if [ "$arg" = "--dangerously-clean" ]; then
    case "$ARTIFACTS" in */artifacts/cli-integration) rm -rf "$ARTIFACTS" ;; *) die "refusing to clean unexpected path" ;; esac
  fi
done

if [ -d "$ARTIFACTS" ]; then
  die "artifacts dir exists: $ARTIFACTS — rerun with --dangerously-clean"
fi
mkdir -p "$ARTIFACTS"

log "building the CLI"
dotnet build "$REPO_ROOT/src/Bundler.Cli/Bundler.Cli.csproj" -c Release -o "$ARTIFACTS/cli" >/dev/null \
  || die "CLI build failed"
CLI="dotnet $ARTIFACTS/cli/bundler.dll"

log "publishing the fixture (self-contained linux-x64)"
dotnet publish "$FIXTURE_DIR/BundlerCliIntegrationFixture.csproj" -c Release \
  -o "$PUBLISH_DIR" >/dev/null || die "fixture publish failed"
[ -x "$PUBLISH_DIR/BundlerCliIntegrationFixture" ] || die "fixture executable missing"

BASE="--input-dir $PUBLISH_DIR --rid linux-x64 --product-name CliFixture --identifier dev.example.cli --package-version 1.0.0 --main-executable BundlerCliIntegrationFixture --output-dir $OUT_DIR"

log "--version / --help"
$CLI --version | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+' || die "--version must print a version"
$CLI --help | grep -q "Usage:" || die "--help must print usage"

log "usage failures exit 2"
$CLI >/dev/null 2>&1; [ $? -eq 2 ] || die "no args must exit 2"
$CLI badcmd >/dev/null 2>&1; [ $? -eq 2 ] || die "unknown command must exit 2"
$CLI bundle $BASE --formats bogus >/dev/null 2>&1; [ $? -eq 2 ] || die "unknown format must exit 2"
$CLI bundle $BASE --formats zip --unexpected-flag >/dev/null 2>&1; [ $? -eq 2 ] || die "unknown option must exit 2"
$CLI bundle $BASE --formats >/dev/null 2>&1; [ $? -eq 2 ] || die "missing option value must exit 2"

log "validate: valid then invalid"
$CLI validate $BASE --formats zip >/dev/null || die "validate must exit 0 on valid config"
$CLI validate $BASE --formats zip --json > "$ARTIFACTS/validate.json" || die "validate --json must exit 0"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); assert d["valid"] is True and d["issues"] == []' \
  "$ARTIFACTS/validate.json" || die "validate json shape wrong"
NOINPUT="${BASE/--input-dir $PUBLISH_DIR/--input-dir /nonexistent-dir}"
$CLI validate $NOINPUT --formats zip >/dev/null 2>"$ARTIFACTS/validate.err"
[ $? -eq 2 ] || die "validate on missing input must exit 2"
grep -qE "inputDirectory|mainExecutable" "$ARTIFACTS/validate.err" || die "validate must report the input issue"

log "validate: matrix violation exits 2"
$CLI validate $BASE --formats nsis >/dev/null 2>"$ARTIFACTS/matrix.err"
[ $? -eq 2 ] || die "nsis on linux-x64 must exit 2"
grep -q "not supported" "$ARTIFACTS/matrix.err" || die "matrix violation must be reported"

log "plan: human and json output"
$CLI plan $BASE --formats deb,rpm,appimage,zip,targz > "$ARTIFACTS/plan.txt" \
  || die "plan must exit 0"
for fmt in deb rpm appimage zip targz; do
  grep -q "linux-x64 $fmt ->" "$ARTIFACTS/plan.txt" || die "plan must list $fmt"
done
$CLI plan $BASE --formats deb --json > "$ARTIFACTS/plan.json" || die "plan --json must exit 0"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); items=d["items"]; assert len(items)==1 and items[0]["format"]=="deb" and items[0]["runtimeIdentifier"]=="linux-x64"' \
  "$ARTIFACTS/plan.json" || die "plan json shape wrong"

log "plan --formats all expands to linux formats only"
$CLI plan $BASE --formats all --json > "$ARTIFACTS/plan-all.json" || die "plan all must exit 0"
python3 - "$ARTIFACTS/plan-all.json" <<'PYEOF' || die "all expansion wrong"
import json, sys
formats = {i["format"] for i in json.load(open(sys.argv[1]))["items"]}
assert formats == {"deb", "rpm", "appimage", "zip", "targz"}, formats
PYEOF

log "bundle: five linux-producible formats"
$CLI bundle $BASE --formats deb,rpm,appimage,zip,targz > "$ARTIFACTS/bundle.txt" 2>"$ARTIFACTS/bundle.err" \
  || die "bundle must exit 0 (see $ARTIFACTS/bundle.err)"
expected=(
  "deb/clifixture_1.0.0-1_amd64.deb"
  "rpm/clifixture-1.0.0-1.x86_64.rpm"
  "zip/clifixture-1.0.0-linux-x64.zip"
  "targz/clifixture-1.0.0-linux-x64.tar.gz"
)
for rel in "${expected[@]}"; do
  [ -f "$OUT_DIR/linux-x64/$rel" ] || die "artifact missing: linux-x64/$rel"
  grep -qF "$OUT_DIR/linux-x64/$rel" "$ARTIFACTS/bundle.txt" || die "stdout must list linux-x64/$rel"
done
appimage_path=$(find "$OUT_DIR/linux-x64/appimage" -name '*.AppImage' -print -quit)
[ -n "$appimage_path" ] || die "appimage artifact missing"
[ -f "$appimage_path.sha256" ] || die "appimage sha256 sidecar missing"

log "bundle --json emits artifact list on stdout only"
$CLI bundle $BASE --formats zip --json > "$ARTIFACTS/bundle.json" 2>"$ARTIFACTS/bundle-json.err" \
  || die "bundle --json must exit 0"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); a=d["artifacts"]; assert len(a)==1 and a[0]["format"]=="zip" and a[0]["path"].endswith(".zip")' \
  "$ARTIFACTS/bundle.json" || die "bundle json shape wrong"
if grep -q "bundler:" "$ARTIFACTS/bundle.json"; then die "json output polluted by log lines"; fi

log "quiet suppresses info logs, verbose shows them"
QUIET="${BASE/--output-dir $OUT_DIR/--output-dir $ARTIFACTS\/out-quiet}"
$CLI bundle $QUIET --formats zip --quiet >"$ARTIFACTS/q.out" 2>"$ARTIFACTS/q.err" \
  || die "quiet bundle must exit 0"
if grep -q "bundler:information" "$ARTIFACTS/q.err"; then die "quiet must suppress information logs"; fi
VERBOSE="${BASE/--output-dir $OUT_DIR/--output-dir $ARTIFACTS\/out-verbose}"
$CLI bundle $VERBOSE --formats zip --verbose 2>"$ARTIFACTS/v.err" >/dev/null \
  || die "verbose bundle must exit 0"
grep -q "bundler:information" "$ARTIFACTS/v.err" || die "verbose must keep information logs"

log "backend failure exits 1"
empty_dir="$ARTIFACTS/empty"; mkdir -p "$empty_dir"
printf 'x' > "$empty_dir/BundlerCliIntegrationFixture"
printf 'locked' > "$empty_dir/locked" && chmod 000 "$empty_dir/locked"
$CLI bundle --input-dir "$empty_dir" --rid linux-x64 --formats zip \
  --product-name CliFixture --identifier dev.example.cli --package-version 1.0.0 \
  --main-executable BundlerCliIntegrationFixture --output-dir "$ARTIFACTS/out-fail" >/dev/null 2>&1
code=$?
chmod 755 "$empty_dir/locked"
[ "$code" -eq 1 ] || die "backend failure must exit 1, got $code"

log "bundler.json drives bundle; CLI options and dotted knobs override"
CFG_DIR="$ARTIFACTS/cfg"; mkdir -p "$CFG_DIR/docs"
printf 'readme-content' > "$CFG_DIR/docs/readme.txt"
cat > "$CFG_DIR/bundler.json" <<EOF
{
  "productName": "CfgApp",
  "identifier": "dev.example.cfg",
  "version": "2.0.0",
  "outputDirectory": "$CFG_DIR/out",
  "targets": [{
    "runtimeIdentifier": "linux-x64",
    "inputDirectory": "$PUBLISH_DIR",
    "mainExecutable": "BundlerCliIntegrationFixture",
    "formats": ["zip"]
  }],
  "archive": {
    "archiveName": "from-config",
    "files": [{ "source": "docs/readme.txt", "destination": "docs/readme.txt" }]
  },
  "deb": { "section": "utils", "maintainer": "Lin <lin@example.com>" }
}
EOF
$CLI bundle --config "$CFG_DIR/bundler.json" --quiet \
  || die "config-driven bundle must exit 0"
CFG_ZIP="$CFG_DIR/out/linux-x64/zip/from-config.zip"
[ -f "$CFG_ZIP" ] || die "config archiveName must produce from-config.zip"
python3 -c 'import zipfile,sys; n=zipfile.ZipFile(sys.argv[1]).namelist(); \
  assert any(e.endswith("/docs/readme.txt") for e in n), n' \
  "$CFG_ZIP" || die "config file mapping must land docs/readme.txt"

$CLI plan --config "$CFG_DIR/bundler.json" --formats targz --json > "$ARTIFACTS/ovr.json" \
  || die "plan with --formats override must exit 0"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); \
  f=[i["format"] for i in d["items"]]; assert f==["targz"], f' \
  "$ARTIFACTS/ovr.json" || die "--formats must override config formats"

$CLI bundle --config "$CFG_DIR/bundler.json" --archive.archive-name=dotted --quiet \
  || die "dotted-knob bundle must exit 0"
[ -f "$CFG_DIR/out/linux-x64/zip/dotted.zip" ] \
  || die "--archive.archive-name must override the config file"

log "unknown config keys and dotted knobs are rejected"
python3 -c 'import sys; s=open(sys.argv[1]).read(); \
  open(sys.argv[2],"w").write(s.replace("\"archive\"","\"achive\""))' \
  "$CFG_DIR/bundler.json" "$CFG_DIR/bad.json"
$CLI plan --config "$CFG_DIR/bad.json" >/dev/null 2>&1; code=$?
[ "$code" -eq 2 ] || die "unknown top-level key must exit 2, got $code"
$CLI plan --config "$CFG_DIR/bundler.json" --deb.bogus=1 >/dev/null 2>&1; code=$?
[ "$code" -eq 2 ] || die "unknown dotted knob must exit 2, got $code"
$CLI plan --config "$CFG_DIR/missing.json" >/dev/null 2>&1; code=$?
[ "$code" -eq 2 ] || die "missing config file must exit 2, got $code"

log "native AOT binary: publish + end-to-end smoke"
dotnet publish "$REPO_ROOT/src/Bundler.Cli/Bundler.Cli.csproj" -c Release -r linux-x64 \
  -o "$ARTIFACTS/aot" -v q >"$ARTIFACTS/aot-publish.log" 2>&1 \
  || die "AOT publish failed (see $ARTIFACTS/aot-publish.log)"
NATIVE="$ARTIFACTS/aot/bundler"
file "$NATIVE" | grep -q "ELF 64-bit" || die "AOT output must be a native ELF binary"
$NATIVE --version | grep -q "$(grep -oPm1 'BundlerPackageVersion>\K[0-9a-zA-Z.-]+' "$REPO_ROOT/Directory.Build.props")" \
  || die "native --version must report the package version"
NATIVE_OUT="$ARTIFACTS/aot-out"
$NATIVE bundle ${BASE/--output-dir $OUT_DIR/--output-dir $NATIVE_OUT} \
  --formats zip --quiet || die "native bundle must exit 0"
find "$NATIVE_OUT" -name "*.zip" -print -quit | grep -q . \
  || die "native bundle must produce a zip artifact"

log "AOT publish embeds only linux-capable backends"
for asm in Wix MacDmg MacPkg; do
  [ ! -e "$ARTIFACTS/aot/DotNet.Bundler.$asm.pdb" ] \
    || die "host-restricted backend $asm must not be published on linux-x64"
done
[ -f "$ARTIFACTS/aot/DotNet.Bundler.AppImage.pdb" ] \
  || die "linux-capable AppImage backend must still be published"
if strings -n 8 "$NATIVE" | grep -qE 'wix3141|candle\.exe|hdiutil'; then
  die "windows/mac toolset payload must be trimmed out of the linux binary"
fi
for fmt in msi dmg pkg; do
  $NATIVE bundle ${BASE/--output-dir $OUT_DIR/--output-dir "$ARTIFACTS/aot-$fmt"} \
    --formats "$fmt" >/dev/null 2>"$ARTIFACTS/aot-$fmt.err"; code=$?
  [ "$code" -eq 1 ] || die "$fmt on a linux AOT build must exit 1, got $code"
  grep -qE "requires a (Windows|macOS) host" "$ARTIFACTS/aot-$fmt.err" \
    || die "$fmt dispatch must fail with an explicit host error"
done

log "all CLI integration assertions passed"
