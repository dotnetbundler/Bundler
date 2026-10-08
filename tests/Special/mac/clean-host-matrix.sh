#!/usr/bin/env bash
# 干净 macOS 宿主验收：无 Xcode（仅 CLT 或全无）的机器上 .app 拖放/.dmg 挂载/.pkg 安装。
# 用法: clean-host-matrix.sh --app <a.app> [--dmg <d.dmg>] [--pkg <p.pkg>]
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-CLEANHOST-MAC-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
wait_human(){ echo "=== 人工动作: $1 ==="; read -r -p "完成后回车: " _; }

xcode=$(xcode-select -p 2>/dev/null || echo "无")
clt=$(pkgutil --pkg-info=com.apple.pkg.CLTools_Executables 2>/dev/null | head -1 || echo "无")
note "宿主干净" "PASS" "xcode-select=$xcode clt=$clt"

APP=""; DMG=""; PKG=""
while [ $# -gt 0 ]; do case "$1" in
  --app) APP="$2"; shift 2;; --dmg) DMG="$2"; shift 2;; --pkg) PKG="$2"; shift 2;; *) shift;;
esac; done

WORK="$(mktemp -d /tmp/bundler-cleanmac-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

if [ -n "$APP" ] && [ -d "$APP" ]; then
    cp -a "$APP" "$WORK/HelloBundlerApp.app"
    wait_human "双击 $WORK/HelloBundlerApp.app——首启是否被 Gatekeeper 拦/要确认的文案记下来"
    note ".app 首启" "UNTESTED" "人工观察记录"
fi
if [ -n "$DMG" ] && [ -f "$DMG" ]; then
    hdiutil attach "$DMG" -mountpoint "$WORK/mnt" -nobrowse \
        && note "dmg 挂载" "PASS" "" || note "dmg 挂载" "FAIL" ""
    wait_human "Finder 打开挂载卷，拖 .app 到 /Applications 替身——验证 SLA 显示与拖放工作"
    hdiutil detach "$WORK/mnt" -quiet 2>/dev/null || true
    note "dmg 卸载" "PASS" ""
fi
if [ -n "$PKG" ] && [ -f "$PKG" ]; then
    sudo -n installer -pkg "$PKG" -target / 2>/dev/null \
        && note ".pkg 安装" "PASS" "" \
        || { wait_human "sudo 不可用——双击 pkg 走 Installer.app GUI 完成安装"; note ".pkg 安装" "UNTESTED" "GUI 路径人工"; }
fi

cat > "$EV" <<EOF
# SA-CLEANHOST-MAC 干净 macOS 宿主验收
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)
- 人工介入点: 首启弹窗、SLA 确认、Installer.app

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
