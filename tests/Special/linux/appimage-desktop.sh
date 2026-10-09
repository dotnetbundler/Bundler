#!/usr/bin/env bash
# AppImage 桌面集成识别验收（机械半）：解出内嵌 .desktop+图标并断言合法性——
# appimaged/AppImageLauncher 类工具识别所需的素材面。
# 观感段（工具真识别+双击观感）仍为人工项，本腿验"识别素材齐备且合法"。
# 用法: appimage-desktop.sh --appimage <x.AppImage>
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-APPIMG-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }

AI=""
while [ $# -gt 0 ]; do case "$1" in --appimage) AI="$2"; shift 2;; *) shift;; esac; done
[ -f "$AI" ] || die "--appimage 指向产出 .AppImage"

WORK="$(mktemp -d /tmp/bundler-appimg-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

# --appimage-extract 由运行时自解 squashfs，免 FUSE 依赖
chmod +x "$AI"
cp "$AI" "$WORK/a.AppImage"
( cd "$WORK" && ./a.AppImage --appimage-extract >/dev/null 2>&1 ) || true
SQ="$WORK/squashfs-root"
[ -d "$SQ" ] && note "--appimage-extract" "PASS" "" || die "--appimage-extract 未产出 squashfs-root"

desktop="$(find "$SQ" -maxdepth 1 -name '*.desktop' | head -1)"
[ -n "$desktop" ] && note "内嵌 .desktop 存在" "PASS" "$(basename "$desktop")" \
    || note "内嵌 .desktop 存在" "FAIL" ""
icon="$(find "$SQ" -maxdepth 2 \( -name '*.png' -o -name '*.svg' -o -name '*.xpm' \) | head -1)"
[ -n "$icon" ] && note "图标文件存在" "PASS" "${icon#$SQ/}" || note "图标文件存在" "FAIL" ""

# 识别必需键
if [ -n "$desktop" ]; then
    for k in Type Name Exec Icon; do
        grep -q "^$k=" "$desktop" && note "键 $k" "PASS" "" || note "键 $k" "FAIL" ""
    done
fi

# desktop-file-validate：宿主有则跑，否则走 docker 装工具验
if [ -n "$desktop" ]; then
    if command -v desktop-file-validate >/dev/null 2>&1; then
        desktop-file-validate "$desktop" && note ".desktop 合法" "PASS" "" \
            || note ".desktop 合法" "FAIL" ""
    elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        docker run --rm -v "$SQ:/sq:ro" debian:stable-slim sh -c \
            "apt-get update -qq >/dev/null && apt-get install -y -qq desktop-file-utils >/dev/null 2>&1 && \
             desktop-file-validate /sq/$(basename "$desktop")" \
            > "$WORK/dv.log" 2>&1 && note ".desktop 合法(docker)" "PASS" "" \
            || note ".desktop 合法" "FAIL" "$(tail -3 "$WORK/dv.log")"
    else
        note ".desktop 合法" "UNTESTED" "desktop-file-utils 与 docker 均缺"
    fi
fi

cat > "$EV" <<EOF
# SA-APPIMG AppImage 桌面集成识别
- 日期: $(date -u +%Y-%m-%d) UTC | 工件: $(basename "$AI")
- 人工余项: appimaged/桌面环境真识别与观感

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
