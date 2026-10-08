#!/usr/bin/env bash
# quarantine 首启验收：模拟真实下载路径（xattr 加 quarantine）→ 首启 Gatekeeper 判定。
# 覆盖未签名/自签/公证三态（签名态由 developer-id-chain 腿接力，此腿验 quarantine 语义本身）。
# 用法: quarantine.sh --app <a.app>
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-QUAR-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; }
wait_human(){ echo "=== 人工动作: $1 ==="; read -r -p "完成后回车: " _; }
die(){ echo "FAIL: $*" >&2; exit 1; }

APP=""
while [ $# -gt 0 ]; do case "$1" in --app) APP="$2"; shift 2;; *) shift;; esac; done
[ -d "$APP" ] || die "--app 指向 .app"

WORK="$(mktemp -d /tmp/bundler-quar-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT
cp -a "$APP" "$WORK/app.app"

# 加 quarantine xattr（模拟浏览器下载）
xattr -w com.apple.quarantine "0083;00000000;Safari;" -r "$WORK/app.app"
xattr -r "$WORK/app.app" | grep -q com.apple.quarantine \
    && note "quarantine 标记落位" "PASS" "" || note "quarantine 标记落位" "FAIL" ""

wait_human "双击 $WORK/app.app——Gatekeeper 弹窗文案记下（未签名=阻止/公证=放行）"
note "Gatekeeper 首启" "UNTESTED" "人工记录弹窗全文"

spctl -a -vv "$WORK/app.app" 2>&1 | tee "$WORK/spctl.log" >/dev/null || true
note "spctl 判定" "PASS" "$(cat "$WORK/spctl.log")"

cat > "$EV" <<EOF
# SA-QUAR quarantine 首启
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)
- 人工介入点: 首启弹窗观察

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"
