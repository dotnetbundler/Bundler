#!/usr/bin/env bash
# ARM64 真机矩阵：deb/rpm/appimage/apk 在真 ARM64 硬件装/跑/卸（非 qemu）。
# 用法: arm64-real-hw.sh --deb <.deb> [--rpm <.rpm>] [--appimage <.AppImage>] [--apk <.apk>]
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-ARM64-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
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
    DEBPKG="$(dpkg-deb -f "$DEB" Package 2>/dev/null || true)"
    $SUDO dpkg -i "$DEB" && note "deb 装" "PASS" "$DEBPKG" || note "deb 装" "FAIL" "$DEBPKG"
    # 断言装后 usr/bin 链接真实可执行（bin 链接默认=包名契约）
    DEBLIST="$($SUDO dpkg -L "$DEBPKG" 2>/dev/null || true)"
    DEBBIN="$(printf '%s\n' "$DEBLIST" | awk '/\/usr\/bin\/[^/]+$/{print $NF; exit}')"
    if [ -n "$DEBBIN" ] && command -v "$DEBBIN" >/dev/null; then
        note "deb 命令在 PATH" "PASS" "$("$DEBBIN" --version 2>&1 | head -1)"
    else
        note "deb 命令在 PATH" "FAIL" "usr/bin 链接缺失: ${DEBBIN:-none}"
    fi
    $SUDO dpkg -r "$DEBPKG" >/dev/null 2>&1 && note "deb 卸" "PASS" "" \
        || { $SUDO dpkg -P "$DEBPKG" >/dev/null 2>&1 && note "deb 卸" "PASS" "purge" \
            || note "deb 卸" "FAIL" "$DEBPKG"; }
fi
if [ -n "$RPM" ] && [ -f "$RPM" ]; then
    RPMPKG="$(rpm -qp --qf '%{NAME}' "$RPM" 2>/dev/null || true)"
    $SUDO rpm -i "$RPM" && note "rpm 装" "PASS" "$RPMPKG" || note "rpm 装" "FAIL" "$RPMPKG"
    RPMLIST="$($SUDO rpm -ql "$RPMPKG" 2>/dev/null || true)"
    RPMBIN="$(printf '%s\n' "$RPMLIST" | awk '/\/usr\/bin\/[^/]+$/{print $NF; exit}')"
    if [ -n "$RPMBIN" ] && command -v "$RPMBIN" >/dev/null; then
        note "rpm 命令在 PATH" "PASS" "$("$RPMBIN" --version 2>&1 | head -1)"
    else
        note "rpm 命令在 PATH" "FAIL" "usr/bin 链接缺失: ${RPMBIN:-none}"
    fi
    $SUDO rpm -e "$RPMPKG" >/dev/null 2>&1 && note "rpm 卸" "PASS" "" \
        || note "rpm 卸" "FAIL" "$RPMPKG"
fi
if [ -n "$AI" ] && [ -f "$AI" ]; then
    chmod +x "$AI"; out=$("$AI" --version 2>&1 | head -1)
    note "AppImage 直跑" "PASS" "$out"
fi
if [ -n "$APK" ] && [ -f "$APK" ]; then
    APKPKG="$(tar -xzOf "$APK" .PKGINFO 2>/dev/null | awk -F' = ' '/^pkgname/{print $2; exit}')"
    $SUDO apk add --allow-untrusted "$APK" && note "apk 装" "PASS" "$APKPKG" \
        || note "apk 装" "FAIL" "$APKPKG"
    $SUDO apk del "$APKPKG" >/dev/null 2>&1 && note "apk 卸" "PASS" "" \
        || note "apk 卸" "FAIL" "$APKPKG"
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
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
