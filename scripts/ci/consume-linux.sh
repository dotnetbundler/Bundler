#!/usr/bin/env bash
# 联合交换消费腿（Linux）：consume-linux.sh <produce-artifact-dir> [--archive-format-only]
# 消费断言只依赖下载的 produce 工件（不依赖构建树/产物）：SHA256SUMS 验同一件
# → 各格式真装真跑真卸 → 清场。腿失败不连锁——逐条收集，末段统一判。
# --archive-format-only=跨 OS 归档交换模式：只验 archive 目录的 zip/tar.gz，
# 做格式级断言（解包+结构+文本标记）——内嵌 fixture 是 RID 绑定原生件，
# 异 OS 不能"跑"只能"解+验结构"，不执行二进制。
set -uo pipefail
DIR="$(cd "${1:?usage: consume-linux.sh <artifact-dir> [--archive-format-only]}" && pwd)"
MODE="${2:-}"
case "$MODE" in
    ""|--archive-format-only) ;;
    *) echo "未知参数：$MODE" >&2; exit 1 ;;
esac
[ $# -le 2 ] || { echo "未知参数：$3" >&2; exit 1; }
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }
( cd "$DIR" && sha256sum -c --strict SHA256SUMS ) || { echo "清单校验失败" >&2; exit 1; }
# 清单外文件即判失败——只验列名件会让产侧混入/夹带漏检。
# 取列名须剥 64 位哈希+两格前缀而非 awk $2——后者在首个空格处截断路径。
EXTRA=$(cd "$DIR" && comm -13 \
    <(sed 's|^[0-9a-f]\{64\}  \./||' SHA256SUMS | sort) \
    <(find . -type f ! -path './SHA256SUMS' | sed 's|^\./||' | sort))
[ -z "$EXTRA" ] || { echo "清单外文件: $EXTRA" >&2; exit 1; }

FAILED=""
RAN=0   # 实际执行腿数——零断言全绿由末段防线判败（与 consume-macos/consume-windows 同口径）
run_leg(){ local name="$1"; shift
    RAN=$((RAN+1))
    if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED="$FAILED $name"; fi }

# docker hub 限流/网络抖动时按登记镜像源回落拉取并 tag 回逻辑名——
# 与 tests/Shared/Tooling/DockerRunner.cs 的 ImageMirrors 同机制、同登记面。
ensure_image(){ local image="$1" mirror=""
    docker image inspect "$image" >/dev/null 2>&1 && return 0
    docker pull "$image" >/dev/null 2>&1 && return 0
    case "$image" in
        fedora:latest) mirror="quay.io/fedora/fedora:latest" ;;
        alpine:latest) mirror="public.ecr.aws/docker/library/alpine:latest" ;;
        *) return 1 ;;
    esac
    docker pull "$mirror" >/dev/null 2>&1 || return 1
    docker tag "$mirror" "$image" >/dev/null 2>&1; }

# 应收件存在性对账：本宿主 produce 腿应收 deb/rpm/appimage/apk/archive 五件——
# 缺件即判败，防产侧布局漂移时"全绿零断言"。
# 归档格式交换模式只验 archive——异 OS 产件其余目录无本宿主可消费格式，非缺失。
EXPECT="deb rpm appimage apk archive"
[ "$MODE" = "--archive-format-only" ] && EXPECT="archive"
for D in $EXPECT; do
    # $D 后接全角括号必须 ${D} 定界——（的 UTF-8 首字节在部分 locale 下是字母。
    [ -d "$DIR/$D" ] || { echo "[LEG-FAIL] ${D}（产件目录缺失）"; FAILED="$FAILED ${D}-missing"; continue; }
    # 目录在但一件没有同样是产侧异常——空 glob 静默跳过会让腿报"全绿零断言"。
    [ -n "$(find "$DIR/$D" -type f | head -1)" ] \
        || { echo "[LEG-FAIL] ${D}（产件目录空）"; FAILED="$FAILED ${D}-empty"; }
done
[ -z "$FAILED" ] || { echo "失败腿:$FAILED" >&2; exit 1; }

if [ "$MODE" != "--archive-format-only" ]; then

# .deb：宿主直装（runner 是 deb 系；root 经 sudo）
if [ -n "$(find "$DIR/deb" -name '*.deb' | head -1)" ] ; then
    DEB="$(find "$DIR/deb" -name '*.deb' -print -quit)"
    run_leg "deb" bash -c '
        set -e
        sudo dpkg -i "$0"
        test -x /usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture
        OUT=$(/usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerDebIntegrationFixture:ci-marker"
        sudo dpkg -r bundler-deb-fixture
        ! test -e /usr/lib/bundler-deb-fixture/BundlerDebIntegrationFixture
    ' "$DEB"
else
    echo "[LEG-FAIL] deb（产件目录无 .deb）"; FAILED="$FAILED deb-noformat"
fi

# .rpm：跨发行版交换——ubuntu 产的包装进 fedora 容器
if [ -n "$(find "$DIR/rpm" -name '*.rpm' | head -1)" ] ; then
    if ensure_image fedora:latest; then
    run_leg "rpm(fedora)" docker run --rm -v "$DIR:/a:ro" fedora:latest bash -c '
        set -e
        dnf install -y /a/rpm/*.rpm >/dev/null
        OUT=$(/usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerRpmIntegrationFixture:ci-marker"
        dnf remove -y bundler-rpm-fixture >/dev/null
        ! test -e /usr/lib/bundler-rpm-fixture/BundlerRpmIntegrationFixture
    '
    else echo "[LEG-FAIL] rpm(fedora)（镜像不可拉取）"; FAILED="$FAILED rpm-image"; fi
else
    echo "[LEG-FAIL] rpm（产件目录无 .rpm）"; FAILED="$FAILED rpm-noformat"
fi

# .apk：跨发行版交换——ubuntu 产的包装进 alpine 容器
if [ -n "$(find "$DIR/apk" -name '*.apk' | head -1)" ] ; then
    if ensure_image alpine:latest; then
    run_leg "apk(alpine)" docker run --rm -v "$DIR:/a:ro" alpine:latest sh -c '
        set -e
        apk add --allow-untrusted /a/apk/*.apk >/dev/null
        OUT=$(/usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture ci-marker)
        echo "$OUT" | grep -q "BundlerAlpineApkIntegrationFixture:ci-marker"
        apk del bundler-apk-fixture >/dev/null
        ! test -e /usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture
    '
    else echo "[LEG-FAIL] apk(alpine)（镜像不可拉取）"; FAILED="$FAILED apk-image"; fi
else
    echo "[LEG-FAIL] apk（产件目录无 .apk）"; FAILED="$FAILED apk-noformat"
fi

# .AppImage 双腿各跑各验（轮测联合测试要求两条腿都断言，不许 OR 链遮蔽）：
# fuse 路=直接运行经 FUSE 挂载（内嵌 type2-runtime 的 squashfuse 探测 fusermount/fusermount3，fuse2/fuse3 皆可用）；
# extract 路=--appimage-extract-and-run 免挂载解压直跑（容器等无 /dev/fuse 环境的路径）。
if [ -n "$(find "$DIR/appimage" -name '*.AppImage' | head -1)" ] ; then
    APPIMAGE="$(find "$DIR/appimage" -name '*.AppImage' -print -quit)"
    chmod +x "$APPIMAGE"
    # fuse 腿帮手兜底：ubuntu 镜像默认带 fuse3，缺装则补——无 /dev/fuse 的内核
    # 环境装上也跑不了，那时 fuse 腿按设计真失败如实报（非环境差异遮蔽）。
    command -v fusermount >/dev/null 2>&1 || command -v fusermount3 >/dev/null 2>&1 \
        || sudo apt-get install -y fuse3 >/dev/null
    run_leg "appimage(fuse)" bash -c '
        set -e
        OUT=$("$0" ci-marker)
        echo "$OUT" | grep -q "BundlerAppImageIntegrationFixture:ci-marker"
    ' "$APPIMAGE"
    run_leg "appimage(extract)" bash -c '
        set -e
        OUT=$("$0" --appimage-extract-and-run ci-marker)
        echo "$OUT" | grep -q "BundlerAppImageIntegrationFixture:ci-marker"
    ' "$APPIMAGE"
else
    echo "[LEG-FAIL] appimage（产件目录无 .AppImage）"; FAILED="$FAILED appimage-noformat"
fi

# 归档件：解开直接跑（zip/tar.gz 都接受）
if [ -n "$(find "$DIR/archive" -type f \( -name '*.zip' -o -name '*.tar.gz' \) | head -1)" ] ; then
    run_leg "archive" bash -c '
        set -e
        # 暂存目录命名与 consume-macos.sh 对齐——verify.yml 失败线索工件按 bundler-consume* glob 收
        T=$(mktemp -d "${TMPDIR:-/tmp}/bundler-consume.XXXXXX"); trap "rm -rf $T" EXIT
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
else
    echo "[LEG-FAIL] archive（产件目录无 .zip/.tar.gz）"; FAILED="$FAILED archive-noformat"
fi

else
# ---- 跨 OS 归档交换：格式级断言（不执行异 RID 二进制）----
# 每份归档独立解包后断言：单一顶层目录 + BundlerArchiveIntegrationFixture 主件
# 在位 + runtimeconfig.json 文本标记（"runtimeOptions"）可读。缺格式即判败。
if [ -n "$(find "$DIR/archive" -name '*.zip' | head -1)" ] ; then
    run_leg "archive-zip(xchg)" bash -c '
        set -e
        T=$(mktemp -d "${TMPDIR:-/tmp}/bundler-consume.XXXXXX"); trap "rm -rf $T" EXIT
        n=0
        for A in "$0"/*.zip; do
            [ -e "$A" ] || continue
            E="$T/x$n"; n=$((n+1)); mkdir -p "$E"
            unzip -q "$A" -d "$E"
            # __MACOSX 目录与 ._* AppleDouble 旁枝同是 macOS 元数据——不计入顶层断言
            TOPS=$(find "$E" -mindepth 1 -maxdepth 1 ! -name __MACOSX ! -name '._*')
            [ "$(printf '%s' "$TOPS" | grep -c .)" -eq 1 ]
            D=$(printf '%s' "$TOPS" | head -1)
            [ -d "$D" ]
            find "$D" -maxdepth 1 -type f \
                \( -name BundlerArchiveIntegrationFixture -o -name BundlerArchiveIntegrationFixture.exe \) \
                | grep -q .
            J="$D/BundlerArchiveIntegrationFixture.runtimeconfig.json"
            [ -s "$J" ]
            grep -q "\"runtimeOptions\"" "$J"
        done
        [ "$n" -gt 0 ]
    ' "$DIR/archive"
else
    echo "[LEG-FAIL] archive-zip(xchg)（产件目录无 .zip）"; FAILED="$FAILED archive-zip-xchg-noformat"
fi

if [ -n "$(find "$DIR/archive" -name '*.tar.gz' | head -1)" ] ; then
    run_leg "archive-targz(xchg)" bash -c '
        set -e
        T=$(mktemp -d "${TMPDIR:-/tmp}/bundler-consume.XXXXXX"); trap "rm -rf $T" EXIT
        n=0
        for A in "$0"/*.tar.gz; do
            [ -e "$A" ] || continue
            E="$T/x$n"; n=$((n+1)); mkdir -p "$E"
            tar -xzf "$A" -C "$E"
            # 同 zip 腿：__MACOSX 目录与 ._* AppleDouble 旁枝豁免顶层计数
            TOPS=$(find "$E" -mindepth 1 -maxdepth 1 ! -name __MACOSX ! -name '._*')
            [ "$(printf '%s' "$TOPS" | grep -c .)" -eq 1 ]
            D=$(printf '%s' "$TOPS" | head -1)
            [ -d "$D" ]
            find "$D" -maxdepth 1 -type f \
                \( -name BundlerArchiveIntegrationFixture -o -name BundlerArchiveIntegrationFixture.exe \) \
                | grep -q .
            J="$D/BundlerArchiveIntegrationFixture.runtimeconfig.json"
            [ -s "$J" ]
            grep -q "\"runtimeOptions\"" "$J"
        done
        [ "$n" -gt 0 ]
    ' "$DIR/archive"
else
    echo "[LEG-FAIL] archive-targz(xchg)（产件目录无 .tar.gz）"; FAILED="$FAILED archive-targz-xchg-noformat"
fi

fi

# 零腿全绿防线：一条腿都没实际执行到零断言全绿——须判败。
[ "$RAN" -gt 0 ] || { echo "无任何腿执行（零断言全绿）" >&2; exit 1; }
if [ -n "$FAILED" ]; then echo "失败腿:$FAILED" >&2; exit 1; fi
echo "=== consume-linux 全绿 ==="
