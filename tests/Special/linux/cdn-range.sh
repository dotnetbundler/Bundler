#!/usr/bin/env bash
# CDN/公网 Range 差分语义验收：真 HTTPS feed 上 Range 请求行为断言
# （file:// 已收编单测；本腿验公网中间盒不改变语义）。
# 需: 公网可访问的 UPDATE feed URL（env UPDATE_FEED_URL 或 --feed）。
# 用法: cdn-range.sh --feed https://cdn.example.com/app/update-feed.json
set -euo pipefail
EVIDENCE_DIR="$(dirname "$0")/../evidence"; mkdir -p "$EVIDENCE_DIR"; EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
EV="$EVIDENCE_DIR/SA-CDNRANGE-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }
command -v curl >/dev/null || die "curl 缺"

FEED="${UPDATE_FEED_URL:-}"
while [ $# -gt 0 ]; do case "$1" in --feed) FEED="$2"; shift 2;; *) shift;; esac; done
[ -n "$FEED" ] || die "--feed 或 UPDATE_FEED_URL 必须给公网 URL"
case "$FEED" in http*://*) ;; *) die "需公网 URL: $FEED";; esac

WORK="$(mktemp -d /tmp/bundler-cdn-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

# 1) feed 可达 + 清单可解析
curl -fsSL "$FEED" -o "$WORK/feed.json" \
    && note "feed 拉取" "PASS" "" || die "feed 不可达: $FEED"
artifact=$(grep -o '"url"[[:space:]]*:[[:space:]]*"[^"]*"' "$WORK/feed.json" | head -1 | cut -d'"' -f4)
[ -n "$artifact" ] || die "feed 中无 artifact url"
base="${FEED%/*}"; url="$artifact"; case "$artifact" in http*://*) ;; *) url="$base/$artifact";; esac
note "工件 URL 解析" "PASS" "$url"

# 2) Range 语义：发 Range: bytes=0-99 看响应码
code=$(curl -s -o "$WORK/part" -w "%{http_code}" -H "Range: bytes=0-99" "$url")
len=$(wc -c < "$WORK/part")
case "$code" in
  206) note "Range 206" "PASS" "len=$len";;
  200) note "Range 206" "UNTESTED" "服务端回 200 全量（忽略 Range）len=$len";;
  *) note "Range 206" "FAIL" "code=$code";;
esac

# 3) 断点续传：下载前半，再 Range 续——内容拼起来与全量一致
curl -fsSL "$url" -o "$WORK/full"
fullsize=$(wc -c < "$WORK/full")
[ "$fullsize" -gt 200 ] || die "工件太小没法做断点续传"
curl -fsSL -H "Range: bytes=0-99" "$url" -o "$WORK/h1" || true
curl -fsSL -H "Range: bytes=100-" "$url" -o "$WORK/h2" || true
cat "$WORK/h1" "$WORK/h2" > "$WORK/joined"
cmp -s "$WORK/joined" "$WORK/full" \
    && note "Range 拼接=全量" "PASS" "size=$fullsize" \
    || note "Range 拼接=全量" "FAIL" "部分源不支持 Range 时落全量语义即可"

cat > "$EV" <<EOF
# SA-CDNRANGE 公网 Range 语义
- 日期: $(date -u +%Y-%m-%d) UTC | feed: $FEED

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
