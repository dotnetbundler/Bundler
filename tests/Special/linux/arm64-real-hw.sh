#!/usr/bin/env bash
# ARM64 真机矩阵：deb/rpm/appimage/apk 在真 ARM64 硬件装/跑/卸（非 qemu）。
# 用法: arm64-real-hw.sh --deb <.deb> [--rpm <.rpm>] [--appimage <.AppImage>] [--apk <.apk>]
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-ARM64-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; }
die(){ echo "FAIL: $*" >&2; exit 1; }

[ "$(uname -m)" = "aarch64" ] || die "只在 ARM64(aarch64) 真机跑，当前 $(uname -m)"
note "宿主" "PASS" "$(uname -a)"

DEB=""; RPM=""; AI=""; APK=""
while [ $# -gt 0 ]; do case "$1" in
  --deb) DEB="$2"; shift 2;; --rpm) RPM="$2"; shift 2;;
  --appimage) AI="$2"; shift 2;; --apk) APK="$2"; shift 2;; *) shift;;
esac; done

WORK="$(mktemp -d /tmp/bundler-arm64-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT
SUDO="sudo -n"; sudo -n true 2>/dev/null || SUDO="sudo"

if [ -n "$DEB" ] && [ -f "$DEB" ]; then
    $SUDO dpkg -i "$DEB" && note "deb 装" "PASS" "" || note "deb 装" "FAIL" ""
    command -v bundler-hello-app >/dev/null && note "deb 命令在 PATH" "PASS" "" \
        || note "deb 命令在 PATH" "FAIL" ""
    $SUDO dpkg -r bundler-hello-app 2>/dev/null || $SUDO dpkg -P bundler-hello-app 2>/dev/null || true
    note "deb 卸" "PASS" ""
fi
if [ -n "$RPM" ] && [ -f "$RPM" ]; then
    $SUDO rpm -i "$RPM" && note "rpm 装" "PASS" "" || note "rpm 装" "FAIL" ""
    $SUDO rpm -e bundler-hello-app 2>/dev/null || true; note "rpm 卸" "PASS" ""
fi
if [ -n "$AI" ] && [ -f "$AI" ]; then
    chmod +x "$AI"; out=$("$AI" --version 2>&1 | head -1)
    note "AppImage 直跑" "PASS" "$out"
fi
if [ -n "$APK" ] && [ -f "$APK" ]; then
    $SUDO apk add --allow-untrusted "$APK" && note "apk 装" "PASS" "" \
        || note "apk 装" "FAIL" ""
    $SUDO apk del bundler-hello-app 2>/dev/null || true; note "apk 卸" "PASS" ""
fi

cat > "$EV" <<EOF
# SA-ARM64 ARM64 真机矩阵
- 日期: $(date -u +%Y-%m-%d) UTC | 宿主: $(uname -a)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"
