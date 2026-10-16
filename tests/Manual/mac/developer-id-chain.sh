#!/usr/bin/env bash
# SA-P-02..06 Developer ID 全链：codesign .app + notarize + staple + spctl + Gatekeeper。
# 需: macOS + Xcode CLT + Developer ID 凭证（env DEV_ID_APP）+
#     公证用 keychain profile（先跑一次：xcrun notarytool store-credentials bundler-notary \
#         --apple-id <id> --team-id <tid> ——密码在 profile 创建时交互录入，不上命令行）；
#     env NOTARY_PROFILE 指定 profile 名（默认 bundler-notary）。
# 用法: developer-id-chain.sh --app <HelloBundlerApp.app> [--dmg <out.dmg>] [--pkg <out.pkg>]
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-P-DEVID-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }
command -v codesign >/dev/null || die "codesign 缺（需 Xcode CLT）"
command -v xcrun >/dev/null || die "xcrun 缺"
command -v spctl >/dev/null || die "spctl 缺"
command -v hdiutil >/dev/null || die "hdiutil 缺"
: "${DEV_ID_APP:?设 DEV_ID_APP='Developer ID Application: ...'}"
# 公证凭据只走 keychain profile——密码不现于进程命令行/env。
NOTARY_PROFILE="${NOTARY_PROFILE:-bundler-notary}"
xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" --output-format json \
    >/dev/null 2>&1 || die "keychain profile '$NOTARY_PROFILE' 未建——先跑 xcrun notarytool store-credentials"

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
xcrun notarytool submit app.zip --keychain-profile "$NOTARY_PROFILE" \
    --wait > notary.log 2>&1 \
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
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
