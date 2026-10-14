#!/usr/bin/env bash
# 联合交换消费腿（macOS）：consume-macos.sh <produce-artifact-dir>
# 在未见构建树的机器上：SHA256SUMS 验同一件 → .app/.dmg/.pkg/归档真装真跑 → 清场。
# 腿失败不连锁——逐条收集，末段统一判。
set -uo pipefail
DIR="$(cd "${1:?usage: consume-macos.sh <artifact-dir>}" && pwd)"
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }
( cd "$DIR" && shasum -a 256 -c SHA256SUMS ) || { echo "清单校验失败" >&2; exit 1; }

FAILED=""
run_leg(){ local name="$1"; shift
    if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED="$FAILED $name"; fi }

# .app：复制到暂存目录后原生启动（marker 写 Contents/.launch-marker）
APP="$(find "$DIR/app" -type d -name '*.app' | head -1)"
if [ -n "$APP" ]; then
    run_leg "app" bash -c '
        set -e
        T=$(mktemp -d); trap "rm -rf $T" EXIT
        cp -R "$0" "$T/"
        B=$(find "$T" -type d -name "*.app" | head -1)
        chmod +x "$B/Contents/MacOS/BundlerMacIntegrationFixture"
        OUT=$("$B/Contents/MacOS/BundlerMacIntegrationFixture" ci-marker)
        echo "$OUT" | grep -q "BundlerMacIntegrationFixture:ci-marker"
    ' "$APP"
fi

# .dmg：挂载 → 拷出 .app → 卸载 → 原生启动
if find "$DIR/dmg" -name '*.dmg' | grep -q .; then
    DMG="$(find "$DIR/dmg" -name '*.dmg' | head -1)"
    run_leg "dmg" bash -c '
        set -e
        T=$(mktemp -d); trap "hdiutil detach "$T/mnt" -force 2>/dev/null; rm -rf $T" EXIT
        hdiutil attach "$0" -nobrowse -readonly -mountpoint "$T/mnt"
        B=$(find "$T/mnt" -type d -name "*.app" | head -1)
        test -d "$B"
        cp -R "$B" "$T/app.app"
        hdiutil detach "$T/mnt" -force
        chmod +x "$T/app.app/Contents/MacOS/BundlerMacDmgIntegrationFixture"
        OUT=$("$T/app.app/Contents/MacOS/BundlerMacDmgIntegrationFixture" ci-marker)
        echo "$OUT" | grep -q "BundlerMacDmgIntegrationFixture:ci-marker"
    ' "$DMG"
fi

# .pkg：installer 真装到 /Applications → 原生启动 → forget+删
if find "$DIR/pkg" -name '*.pkg' | grep -q .; then
    PKG="$(find "$DIR/pkg" -name '*.pkg' | head -1)"
    run_leg "pkg" bash -c '
        set -e
        sudo installer -pkg "$0" -target /
        B="/Applications/Bundler Mac PKG Fixture.app"
        test -d "$B"
        OUT=$("$B/Contents/MacOS/BundlerMacPkgIntegrationFixture" ci-marker)
        echo "$OUT" | grep -q "BundlerMacPkgIntegrationFixture:ci-marker"
        sudo pkgutil --forget com.dotnetbundler.macpkgintegrationfixture 2>/dev/null || true
        sudo rm -rf "$B"
    ' "$PKG"
fi

# 归档件：解开直接跑
if find "$DIR/archive" -type f \( -name '*.zip' -o -name '*.tar.gz' \) | grep -q .; then
    run_leg "archive" bash -c '
        set -e
        T=$(mktemp -d); trap "rm -rf $T" EXIT
        for A in "$0"/*.zip "$0"/*.tar.gz; do
            [ -e "$A" ] || continue
            case "$A" in
                *.zip)    unzip -q "$A" -d "$T" ;;
                *.tar.gz) tar -xzf "$A" -C "$T" ;;
            esac
        done
        EXE=$(find "$T" -name BundlerArchiveIntegrationFixture -type f | head -1)
        test -n "$EXE"
        chmod +x "$EXE"
        OUT=$("$EXE" ci-marker)
        echo "$OUT" | grep -q "BundlerArchiveIntegrationFixture:ci-marker"
    ' "$DIR/archive"
fi

if [ -n "$FAILED" ]; then echo "失败腿:$FAILED" >&2; exit 1; fi
echo "=== consume-macos 全绿 ==="
