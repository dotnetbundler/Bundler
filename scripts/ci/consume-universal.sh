#!/usr/bin/env bash
# universal .app 交换消费腿（macOS 双宿主共用）：consume-universal.sh <artifact-dir>
# 同一份 macos-universal .app 在 x86_64 与 arm64 宿主各自原生启动；
# "同一包"由 SHA256SUMS 比对钉死（非各腿重建）。
set -euo pipefail
DIR="$(cd "${1:?usage: consume-universal.sh <artifact-dir>}" && pwd)"
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }
( cd "$DIR" && shasum -a 256 -c SHA256SUMS )

APP="$(find "$DIR" -type d -name '*.app' | head -1)"
[ -d "$APP" ] || { echo "未找到 .app" >&2; exit 1; }
EXE="$APP/Contents/MacOS/BundlerMacIntegrationFixture"
lipo -info "$EXE"
lipo -info "$EXE" | grep -q "x86_64"
lipo -info "$EXE" | grep -q "arm64"
chmod +x "$EXE"   # 工件下载不保留执行位
OUT=$("$EXE" ci-marker)
echo "$OUT"
echo "$OUT" | grep -q "BundlerMacIntegrationFixture:ci-marker"
echo "=== consume-universal 全绿 ==="
