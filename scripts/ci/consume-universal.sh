#!/usr/bin/env bash
# universal .app 交换消费腿（macOS 双宿主共用）：consume-universal.sh <artifact-dir>
# 同一份 macos-universal .app 在 x86_64 与 arm64 宿主各自原生启动；
# "同一包"由 SHA256SUMS 比对钉死（非各腿重建）。
set -euo pipefail
DIR="$(cd "${1:?usage: consume-universal.sh <artifact-dir>}" && pwd)"
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }

# find|head 的 SIGPIPE 竞态在 set -euo pipefail 域内是杀脚本级——
# 多个 .app 命中时 head 早退、find 吃 141 连带赋值行失败退出；
# -print -quit 让 find 自取首条即正常退出（GNU/BSD 通用）。
APP="$(find "$DIR" -type d -name '*.app' -print -quit)"
[ -d "$APP" ] || { echo "未找到 .app" >&2; exit 1; }
# 清单在 .app 目录内生成（条目为 ./Contents/...）——校验基准目录对齐产侧。
( cd "$APP" && shasum -a 256 -c "$DIR/SHA256SUMS" )
# 严格度对齐三兄弟（consume-macos/linux/windows）：shasum 无 --strict——畸形行
# （非空且不合 "hash  ./relpath" 形）单独判失败；清单外文件同样判失败，
# 防产侧混入/夹带漏检。用 (...)? 而非 (...|)——BSD grep 拒绝空备选分支。
[ "$(grep -cvE '^([0-9a-f]{64}  \./.+)?$' "$DIR/SHA256SUMS")" = "0" ] \
    || { echo "清单含畸形行" >&2; exit 1; }
# 对账基准在工件根：清单条目补上 .app 相对路径前缀，.app 外/内的杂件都能被抓。
APPREL="${APP#"$DIR"/}"
EXTRA=$(cd "$DIR" && comm -13 \
    <(sed 's|^[0-9a-f]\{64\}  \./||' SHA256SUMS | sed "s|^|${APPREL}/|" | sort) \
    <(find . -type f ! -path './SHA256SUMS' | sed 's|^\./||' | sort))
[ -z "$EXTRA" ] || { echo "清单外文件: $EXTRA" >&2; exit 1; }

EXE="$APP/Contents/MacOS/BundlerMacIntegrationFixture"
lipo -info "$EXE"
lipo -info "$EXE" | grep -q "x86_64"
lipo -info "$EXE" | grep -q "arm64"
chmod +x "$EXE"   # 工件下载不保留执行位
OUT=$("$EXE" ci-marker)
echo "$OUT"
echo "$OUT" | grep -q "BundlerMacIntegrationFixture:ci-marker"
echo "=== consume-universal 全绿 ==="
