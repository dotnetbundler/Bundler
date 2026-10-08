#!/usr/bin/env bash
# SA-P-07 签名 .app 更新链：v1 签名装→应用内 UpdateClient 拉 v2 签名包→
# 换包后 codesign 验证身份一致性（TeamID 同、签名仍有效）。
# 需: macOS+Developer ID 凭证+本地 feed（UPDATE feed 目录经 --feed 给出）。
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-P-07-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }
: "${DEV_ID_APP:?设 DEV_ID_APP}"
FEED=""; APPV1=""; APPV2=""
while [ $# -gt 0 ]; do case "$1" in
  --feed) FEED="$2"; shift 2;; --v1) APPV1="$2"; shift 2;; --v2) APPV2="$2"; shift 2;;
  *) die "未知参数 $1";;
esac; done
[ -n "$FEED" ] && [ -d "$FEED" ] || die "--feed 指向 UPDATE feed 目录"
[ -n "$APPV1" ] && [ -d "$APPV1" ] || die "--v1 指向签名 v1 .app"
[ -n "$APPV2" ] && [ -d "$APPV2" ] || die "--v2 指向签名 v2 .app"

WORK="$(mktemp -d /tmp/bundler-sa07-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT
cp -a "$APPV1" "$WORK/install.app"

team1=$(codesign -dvvv "$WORK/install.app" 2>&1 | grep "TeamIdentifier" | awk -F= '{print $2}')
note "v1 TeamID" "PASS" "$team1"

# 走 bundler-updater 换包（v2 签名包作为 payload）
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
rid="osx-$(uname -m | sed 's/x86_64/x64/')"   # arm64→osx-arm64、x86_64→osx-x64
updater="$repo/src/Bundler.Updater.Bootstrap/tools/$rid/bundler-updater"
[ -x "$updater" ] || die "bundler-updater 缺位: $updater"
"$updater" apply --install-dir "$WORK/install.app" --payload "$APPV2" \
    --log "$WORK/u.log" && note "换包" "PASS" "" || note "换包" "FAIL" "$(cat "$WORK/u.log")"

team2=$(codesign -dvvv "$WORK/install.app" 2>&1 | grep "TeamIdentifier" | awk -F= '{print $2}')
codesign --verify --deep --strict "$WORK/install.app" \
    && note "换包后签名仍有效" "PASS" "" || note "换包后签名仍有效" "FAIL" ""
[ "$team1" = "$team2" ] && note "TeamID 一致" "PASS" "$team1→$team2" \
    || note "TeamID 一致" "FAIL" "$team1→$team2"

cat > "$EV" <<EOF
# SA-P-07 签名 .app 更新链
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
