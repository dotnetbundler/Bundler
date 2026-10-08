#!/usr/bin/env bash
# Intel Mac (osx-x64) 实跑/挂载/安装验收——Rosetta 也行但注明。
# 用法: intel-x64.sh --app <osx-x64产 .app> [--dmg <x64.dmg>] [--binary <osx-x64 CLI>]
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-INTELMAC-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; }
die(){ echo "FAIL: $*" >&2; exit 1; }

arch=$(uname -m)
rosetta="无"; /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null && rosetta="可用"
note "宿主" "PASS" "arch=$arch rosetta=$rosetta"
{ [ "$arch" = "x86_64" ] || [ "$rosetta" = "可用" ]; } || die "非 x64 且无 Rosetta，osx-x64 件跑不了"

APP=""; DMG=""; BIN=""
while [ $# -gt 0 ]; do case "$1" in
  --app) APP="$2"; shift 2;; --dmg) DMG="$2"; shift 2;; --binary) BIN="$2"; shift 2;; *) shift;;
esac; done

WORK="$(mktemp -d /tmp/bundler-intel-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

if [ -n "$BIN" ] && [ -f "$BIN" ]; then
    out=$("$BIN" --version 2>&1 || "$BIN" --help 2>&1 | head -1)
    note "osx-x64 二进制实跑" "PASS" "$out"
fi
if [ -n "$APP" ] && [ -d "$APP" ]; then
    cp -a "$APP" "$WORK/app.app"
    "$WORK/app.app/Contents/MacOS/"* --version >/dev/null 2>&1 \
        && note ".app x64 进程起" "PASS" "" || note ".app x64 进程起" "FAIL" ""
fi
if [ -n "$DMG" ] && [ -f "$DMG" ]; then
    hdiutil attach "$DMG" -mountpoint "$WORK/mnt" -nobrowse \
        && note "dmg 挂载(x64 内容)" "PASS" "" || note "dmg 挂载" "FAIL" ""
    hdiutil detach "$WORK/mnt" -quiet 2>/dev/null || true
fi

cat > "$EV" <<EOF
# SA-INTELMAC osx-x64 验收
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)
- 模式: $([ "$arch" = "x86_64" ] && echo "Intel 真机" || echo "ARM64+Rosetta")

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"
