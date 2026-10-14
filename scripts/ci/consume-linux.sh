#!/usr/bin/env bash
# 联合交换消费腿（Linux）：consume-linux.sh <produce-artifact-dir>
# 在未见构建树的机器上：SHA256SUMS 验同一件 → 各格式真装真跑真卸 → 清场。
# 腿失败不连锁——逐条收集，末段统一判。
set -uo pipefail
DIR="$(cd "${1:?usage: consume-linux.sh <artifact-dir>}" && pwd)"
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }
( cd "$DIR" && sha256sum -c SHA256SUMS ) || { echo "清单校验失败" >&2; exit 1; }

FAILED=""
run_leg(){ local name="$1"; shift
    if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED="$FAILED $name"; fi }

# .deb：宿主直装（runner 是 deb 系；root 经 sudo）
if find "$DIR/deb" -name '*.deb' | grep -q .; then
    DEB="$(find "$DIR/deb" -name '*.deb' | head -1)"
    run_leg "deb" bash -c '
        set -e
        sudo dpkg -i "$0"
        test -x /usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture
        OUT=$(/usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerDebIntegrationFixture:ci-marker"
        sudo dpkg -r bundler-deb-fixture
        ! test -e /usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture
    ' "$DEB"
fi

# .rpm：跨发行版交换——ubuntu 产的包装进 fedora 容器
if find "$DIR/rpm" -name '*.rpm' | grep -q .; then
    run_leg "rpm(fedora)" docker run --rm -v "$DIR:/a:ro" fedora:latest bash -c '
        set -e
        dnf install -y /a/rpm/*.rpm >/dev/null
        OUT=$(/usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerRpmIntegrationFixture:ci-marker"
        dnf remove -y bundler-rpm-fixture >/dev/null
        ! test -e /usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture
    '
fi

# .apk：跨发行版交换——ubuntu 产的包装进 alpine 容器
if find "$DIR/apk" -name '*.apk' | grep -q .; then
    run_leg "apk(alpine)" docker run --rm -v "$DIR:/a:ro" alpine:latest sh -c '
        set -e
        apk add --allow-untrusted /a/apk/*.apk >/dev/null
        OUT=$(/usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerAlpineApkIntegrationFixture:ci-marker"
        apk del bundler-apk-fixture >/dev/null
        ! test -e /usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture
    '
fi

# .AppImage：宿主直跑（FUSE 缺席回落 --appimage-extract-and-run）
if find "$DIR/appimage" -name '*.AppImage' | grep -q .; then
    APPIMAGE="$(find "$DIR/appimage" -name '*.AppImage' | head -1)"
    run_leg "appimage" bash -c '
        set -e
        chmod +x "$0"
        OUT=$("$0" ci-marker 2>/dev/null || "$0" --appimage-extract-and-run ci-marker)
        echo "$OUT" | grep -q "BundlerAppImageIntegrationFixture:ci-marker"
    ' "$APPIMAGE"
fi

# 归档件：解开直接跑（zip/tar.gz 都接受）
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
echo "=== consume-linux 全绿 ==="
