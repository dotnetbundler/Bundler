#!/usr/bin/env bash
# SA-P-02..06 Developer ID 全链：codesign .app + notarize + staple + spctl + Gatekeeper。
# 需: macOS + Xcode CLT + Developer ID 凭证（env DEV_ID_APP / DEV_ID_INSTALLER / APPLE_ID / APPLE_APP_PW / TEAM_ID）。
# 用法: developer-id-chain.sh --app <HelloBundlerApp.app> [--dmg <out.dmg>] [--pkg <out.pkg>]
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-P-DEVID-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; }
die(){ echo "FAIL: $*" >&2; exit 1; }
command -v codesign >/dev/null || die "codesign 缺（需 Xcode CLT）"
command -v xcrun >/dev/null || die "xcrun 缺"
command -v spctl >/dev/null || die "spctl 缺"
command -v hdiutil >/dev/null || die "hdiutil 缺"
: "${DEV_ID_APP:?设 DEV_ID_APP='Developer ID Application: ...'}"
: "${APPLE_ID:?设 APPLE_ID}"; : "${APPLE_APP_PW:?设 APPLE_APP_PW}"; : "${TEAM_ID:?设 TEAM_ID}"

APP=""; DMG=""; PKG=""
while [ $# -gt 0 ]; do case "$1" in
  --app) APP="$2"; shift 2;; --dmg) DMG="$2"; shift 2;; --pkg) PKG="$2"; shift 2;; *) die "未知参数 $1";;
esac; done
[ -n "$APP" ] && [ -d "$APP" ] || die "--app 指向 .app 目录"
WORK="$(mktemp -d /tmp/bundler-devid-XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

cp -a "$APP" "$WORK/app.app"
codesign --deep --force --options runtime --sign "$DEV_ID_APP" "$WORK/app.app" \
    && note "codesign .app" "PASS" "" || note "codesign .app" "FAIL" ""
codesign --verify --deep --strict "$WORK/app.app" \
    && note "签验" "PASS" "" || note "签验" "FAIL" ""

# 公证
cd "$WORK" && /usr/bin/ditto -c -k --keepParent app.app app.zip
xcrun notarytool submit app.zip --apple-id "$APPLE_ID" --password "$APPLE_APP_PW" \
    --team-id "$TEAM_ID" --wait > notary.log 2>&1 \
    && note "公证 submit+wait" "PASS" "$(grep -i 'status' notary.log | head -1)" \
    || note "公证 submit+wait" "FAIL" "$(tail -3 notary.log)"
xcrun stapler staple "$WORK/app.app" && note "staple" "PASS" "" || note "staple" "FAIL" ""
spctl -a -vv "$WORK/app.app" 2>&1 | tee spctl.log >/dev/null
grep -q "accepted" spctl.log && note "spctl Gatekeeper" "PASS" "$(cat spctl.log)" \
    || note "spctl Gatekeeper" "FAIL" "$(cat spctl.log)"

if [ -n "$DMG" ]; then
    hdiutil create -volname HelloBundlerApp -srcfolder "$WORK/app.app" -ov -format UDZO "$WORK/$DMG" \
        && note "dmg 产包" "PASS" "" || note "dmg 产包" "FAIL" ""
    codesign --sign "$DEV_ID_APP" "$WORK/$DMG" && note "dmg codesign" "PASS" "" \
        || note "dmg codesign" "FAIL" ""
fi

cat > "$EV" <<EOF
# SA-P-02..06 Developer ID 全链
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)
- 凭证: Developer ID Application（Subject 指纹记于下，私钥不落盘）

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"
