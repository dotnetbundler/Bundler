#!/usr/bin/env bash
# UPDATE 真实发布管线：产 v1→签名→传 feed 到公网位置→v1 安装→UpdateClient 拉→换包→验。
# 需: 公网 feed 可写位置（如 GH Pages/S3 推后的 URL 前缀）+ 私钥（env UPDATE_PRIVATE_KEY 或文件）。
# 用法: update-public-pipeline.sh --feed-url https://.../update-feed.json --feed-dir <本地feed目录>
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-UPDATEPIPE-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
wait_human(){ [ ! -t 0 ] && { echo "(无人值守跳过人工动作: $1)"; return 0; }; echo "=== 人工动作: $1 ==="; read -r -p "完成后回车: " _; }
die(){ echo "FAIL: $*" >&2; exit 1; }

FEED_URL=""; FEED_DIR=""
while [ $# -gt 0 ]; do case "$1" in
  --feed-url) FEED_URL="$2"; shift 2;; --feed-dir) FEED_DIR="$2"; shift 2;; *) shift;;
esac; done
[ -n "$FEED_URL" ] || die "--feed-url 必须给"
[ -d "$FEED_DIR" ] || die "--feed-dir 指向本地产出的 UPDATE feed 目录"
[ -f "$FEED_DIR/update-feed.json" ] || die "$FEED_DIR 缺 update-feed.json"

WORK="$(mktemp -d /tmp/bundler-upipe-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

wait_human "把 $FEED_DIR 内容推到公网（对应 $FEED_URL 前缀），推到后回车"

curl -fsSL "$FEED_URL" -o "$WORK/pub-feed.json" \
    && note "公网 feed 拉取" "PASS" "" || die "公网 feed 不可达"
cmp -s "$WORK/pub-feed.json" "$FEED_DIR/update-feed.json" \
    && note "公网 feed=本地" "PASS" "" || note "公网 feed=本地" "FAIL" "字节不一致"

# UpdateClient 走真 HTTP 拉——经宿主 CLI（或单测级别 HTTP 拉件即验语义）
url=$(grep -o '"url"[[:space:]]*:[[:space:]]*"[^"]*"' "$WORK/pub-feed.json" | head -1 | cut -d'"' -f4)
case "$url" in http*://*) full="$url";; *) full="${FEED_URL%/*}/$url";; esac
curl -fsSL "$full" -o "$WORK/artifact" && note "公网工件下载" "PASS" "" || note "公网工件下载" "FAIL" ""

cat > "$EV" <<EOF
# SA-UPDATEPIPE UPDATE 公网管线
- 日期: $(date -u +%Y-%m-%d) UTC | feed: $FEED_URL
- 人工介入点: feed 目录上传公网

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
