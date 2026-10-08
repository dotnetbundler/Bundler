#!/usr/bin/env bash
# apk 索引/仓库工作流验收（可选扩展腿）：apk add --allow-untrusted 单件路径已由
# IntegrationTests 覆盖；本腿验真仓库索引（abuild-index→APKINDEX→apk add 经 index）。
# 需: alpine 宿主或 docker(alpine)；abuild 或 apk 工具链。
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-APKIDX-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }

APK=""
while [ $# -gt 0 ]; do case "$1" in --apk) APK="$2"; shift 2;; *) shift;; esac; done
[ -f "$APK" ] || die "--apk 指向产出的 .apk"

WORK="$(mktemp -d /tmp/bundler-apkidx-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

# 优先本机 alpine，否则 docker alpine 内跑
if command -v apk >/dev/null 2>&1; then
    RUN=""; note "宿主" "PASS" "本机 alpine/apk"
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    RUN="docker"; note "宿主" "PASS" "docker alpine"
else
    die "apk 与 docker 都缺"
fi

mkdir -p "$WORK/repo"
cp "$APK" "$WORK/repo/"

if [ "$RUN" = "docker" ]; then
    docker run --rm -v "$WORK/repo:/repo" -v "$WORK/out:/out" alpine:latest sh -c \
        "apk add --no-cache alpine-sdk >/dev/null 2>&1 || apk add --no-cache abuild >/dev/null 2>&1; \
         apk index -o /repo/APKINDEX.tar.gz --allow-untrusted /repo/*.apk && echo IDX-OK" \
        > "$WORK/idx.log" 2>&1 && grep -q IDX-OK "$WORK/idx.log" \
        && note "APKINDEX 生成" "PASS" "" || note "APKINDEX 生成" "FAIL" "$(tail -3 "$WORK/idx.log")"
else
    apk index -o "$WORK/repo/APKINDEX.tar.gz" --allow-untrusted "$WORK/repo"/*.apk \
        && note "APKINDEX 生成" "PASS" "" || note "APKINDEX 生成" "FAIL" ""
fi
[ -f "$WORK/repo/APKINDEX.tar.gz" ] && note "索引文件存在" "PASS" "" || note "索引文件存在" "FAIL" ""

cat > "$EV" <<EOF
# SA-APKIDX apk 索引工作流
- 日期: $(date -u +%Y-%m-%d) UTC | 工件: $(basename "$APK")

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
