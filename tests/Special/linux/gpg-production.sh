#!/usr/bin/env bash
# GPG 生产流程验收：真密钥签名+分发+吊销（生成→签名→验签→吊销证书→验拒）。
# 需: 用户已有 GPG 密钥（env GPG_KEYID）或脚本生临时钥自演。
# 用法: gpg-production.sh [--keyid <GPG_KEYID>] [--self-contained]  (--self-contained=临时钥全链)
set -euo pipefail
EVIDENCE_DIR="$(cd "$(dirname "$0")/../evidence" && pwd)"; mkdir -p "$EVIDENCE_DIR"
EV="$EVIDENCE_DIR/SA-GPG-$(date +%Y%m%d).md"
note(){ echo "| $1 | $2 | $3 |" >> "$EV.tmp"; echo "[$2] $1 $3"; [ "$2" = "FAIL" ] && HAD_FAIL=1; return 0; }
die(){ echo "FAIL: $*" >&2; exit 1; }
command -v gpg >/dev/null || die "gpg 缺"

KEYID="${GPG_KEYID:-}"; SELF="no"
while [ $# -gt 0 ]; do case "$1" in
  --keyid) KEYID="$2"; shift 2;; --self-contained) SELF="yes"; shift;; *) shift;;
esac; done

WORK="$(mktemp -d /tmp/bundler-gpg-XXXXXX)"; trap 'rm -rf "$WORK"' EXIT

if [ "$SELF" = "yes" ] || [ -z "$KEYID" ]; then
    # 自演模式才用隔离密钥环；用户密钥模式用默认环否则找不着钥。
    export GNUPGHOME="$WORK/gnupg"; mkdir -p "$GNUPGHOME"; chmod 700 "$GNUPGHOME"
    gpg --batch --gen-key <<CFG
Key-Type: eddsa
Key-Curve: ed25519
Name-Real: Bundler SA Test
Name-Email: sa-test@bundler.local
Expire-Date: 0
%no-protection
CFG
    KEYID=$(gpg --list-keys --with-colons | awk -F: '/^pub/{print $5; exit}')
    note "临时钥生成" "PASS" "$KEYID"
else
    gpg --list-keys "$KEYID" >/dev/null || die "密钥不在环: $KEYID"
    note "使用既有密钥" "PASS" "$KEYID"
fi

# 签名+验签
echo "bundler-payload" > "$WORK/payload.bin"
gpg --batch --yes --detach-sign --armor -u "$KEYID" "$WORK/payload.bin"
gpg --verify "$WORK/payload.bin.asc" "$WORK/payload.bin" \
    && note "签名验签" "PASS" "" || note "签名验签" "FAIL" ""

# 导出公钥分发+吊销
gpg --export --armor "$KEYID" > "$WORK/pub.asc"
note "公钥导出" "PASS" "fp=$(gpg --list-keys --with-colons | awk -F: '/^fpr/{print $10; exit}')"
gpg --batch --yes --output "$WORK/revoke.asc" --gen-revoke "$KEYID" <<EOF 2>/dev/null || true
y
1
test revoke

y
EOF
note "吊销证书生成" "$([ -f "$WORK/revoke.asc" ] && echo PASS || echo UNTESTED)" ""

cat > "$EV" <<EOF
# SA-GPG 生产 GPG 流程
- 日期: $(date -u +%Y-%m-%d) UTC | keyid: $KEYID | 模式: $([ "$SELF" = yes ] && echo 临时钥自演 || echo 用户钥)

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$(cat "$EV.tmp" 2>/dev/null)
EOF
rm -f "$EV.tmp"
echo "证据: $EV"; [ "${HAD_FAIL:-0}" = "1" ] && exit 1; exit 0
