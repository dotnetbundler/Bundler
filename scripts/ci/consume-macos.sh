#!/usr/bin/env bash
# 联合交换消费腿（macOS）：consume-macos.sh <produce-artifact-dir> [腿=子目录 …]
# 消费断言只依赖下载的 produce 工件（不依赖构建树/产物）：SHA256SUMS 验同一件 → .app/.dmg/.pkg/归档真装真跑 → 清场。
# 腿失败不连锁——逐条收集，末段统一判。
# 可选腿重映射（异构消费用）：app=app-x64 dmg=dmg-x64 pkg=- archive=- ——
# 键=腿名（app/dmg/pkg/archive），值=工件内子目录名，"-"=该产件无本宿主可用切片，刻意跳过。
# --archive-format-only=跨 OS 归档交换模式：app/dmg/pkg 腿强制映射 "-"，
# archive 腿改做格式级断言（解包+结构+文本标记）——内嵌 fixture 是 RID 绑定
# 原生件，异 OS 不能"跑"只能"解+验结构"，不执行二进制。
set -uo pipefail
DIR="$(cd "${1:?usage: consume-macos.sh <artifact-dir> [腿=子目录 …] [--archive-format-only]}" && pwd)"; shift
# bash 3.2（macOS runner 自带）无关联数组——四具名变量+case 分派等价承载。
DIR_APP=app; DIR_DMG=dmg; DIR_PKG=pkg; DIR_ARCHIVE=archive
FMT_ONLY=""
for KV in "$@"; do
    [ "$KV" = "--archive-format-only" ] && { FMT_ONLY=1; continue; }
    K="${KV%%=*}"; V="${KV#*=}"
    # 空值拒绝：app= 之类会把腿目录静默扩成工件根目录（"$DIR/" 下全件被当腿内
    # 容消费）；显式跳过用 -。
    [ -n "$V" ] || { echo "腿映射值为空（显式跳过用 -）：$KV" >&2; exit 1; }
    case "$K" in
        app)     DIR_APP="$V" ;;
        dmg)     DIR_DMG="$V" ;;
        pkg)     DIR_PKG="$V" ;;
        archive) DIR_ARCHIVE="$V" ;;
        *) echo "未知腿映射：$KV" >&2; exit 1 ;;
    esac
done
# 归档格式交换模式：非 archive 腿强制跳过（等价显式映射 -），archive 映射仍可调。
[ -n "$FMT_ONLY" ] && { DIR_APP=-; DIR_DMG=-; DIR_PKG=-; }
[ -f "$DIR/SHA256SUMS" ] || { echo "缺 SHA256SUMS：$DIR" >&2; exit 1; }
( cd "$DIR" && shasum -a 256 -c SHA256SUMS ) || { echo "清单校验失败" >&2; exit 1; }
# shasum 无 --strict——畸形行（非空且不合 "hash  ./relpath" 形）单独判失败；
# 清单外文件同样判失败，防产侧混入/夹带漏检。
# 用 (...)? 而非 (...|) ——BSD grep 拒绝空备选分支（empty (sub)expression）。
# 路径段放宽为 .+ ——清单路径可含空格（.app 束内逐文件路径随 ProductName 带空格）。
[ "$(grep -cvE '^([0-9a-f]{64}  \./.+)?$' "$DIR/SHA256SUMS")" = "0" ] \
    || { echo "清单含畸形行" >&2; exit 1; }
# 取列名须剥 64 位哈希+两格前缀而非 awk $2——后者在首个空格处截断路径。
EXTRA=$(cd "$DIR" && comm -13 \
    <(sed 's|^[0-9a-f]\{64\}  \./||' SHA256SUMS | sort) \
    <(find . -type f ! -path './SHA256SUMS' | sed 's|^\./||' | sort))
[ -z "$EXTRA" ] || { echo "清单外文件: $EXTRA" >&2; exit 1; }

FAILED=""
RAN=0   # 实际执行腿数——全 LEG-SKIP 的零断言全绿由末段防线判败
run_leg(){ local name="$1"; shift
    RAN=$((RAN+1))
    if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED="$FAILED $name"; fi }
leg_dir(){ case "$1" in                 # 腿名 → 工件内子目录
    app) echo "$DIR_APP";; dmg) echo "$DIR_DMG";;
    pkg) echo "$DIR_PKG";; archive) echo "$DIR_ARCHIVE";;
    *) echo "leg_dir 未知腿名：$1" >&2; exit 1;; esac; }
leg_on(){ local d; d="$(leg_dir "$1")" || exit 1; [ "$d" != "-" ]; }
                                          # leg_on 为直接调用非替换——体内 exit 真退出；
                                          # 腿被映射为 - 时本产件无可消费切片

# 应收件存在性对账：映射表内每条腿的子目录都必须在（刻意跳过的除外）。
for D in app dmg pkg archive; do
    # 全角括号首字节在某些 locale 下是字母（Latin-1 ï）——$D 必须 ${D} 定界，
    # 否则被拼进变量名在 set -u 下炸 unbound（本行 LEG-SKIP 是首条真踩中的路径）。
    leg_on "$D" || { echo "[LEG-SKIP] ${D}（产件无本宿主可用切片，映射跳过）"; continue; }
    [ -d "$DIR/$(leg_dir "$D")" ] || { echo "[LEG-FAIL] ${D}（产件目录缺失：$(leg_dir "$D")）"; FAILED="$FAILED ${D}-missing"; continue; }
    # 目录在但一件没有同样是产侧异常——空 glob 静默跳过会让腿报"全绿零断言"。
    [ -n "$(find "$DIR/$(leg_dir "$D")" -type f | head -1)" ] \
        || { echo "[LEG-FAIL] ${D}（产件目录空：$(leg_dir "$D")）"; FAILED="$FAILED ${D}-empty"; }
done
[ -z "$FAILED" ] || { echo "失败腿:$FAILED" >&2; exit 1; }

# .app：复制到暂存目录后原生启动（marker 写 Contents/.launch-marker）
if leg_on app; then
    APP="$(find "$DIR/$(leg_dir app)" -type d -name '*.app' | head -1)"
    if [ -n "$APP" ]; then
        run_leg "app" bash -c '
        set -e
        T=$(mktemp -d "${TMPDIR:-/tmp}/bundler-consume.XXXXXX"); trap "rm -rf $T" EXIT
        cp -R "$0" "$T/"
        B=$(find "$T" -type d -name "*.app" | head -1)
        chmod +x "$B/Contents/MacOS/BundlerMacIntegrationFixture"
        OUT=$("$B/Contents/MacOS/BundlerMacIntegrationFixture" ci-marker)
        echo "$OUT" | grep -q "BundlerMacIntegrationFixture:ci-marker"
    ' "$APP"
    else
        echo "[LEG-FAIL] app（产件目录无 .app）"; FAILED="$FAILED app-noformat"
    fi
fi

# .dmg：挂载 → 拷出 .app → 卸载 → 原生启动
if leg_on dmg; then
    if [ -n "$(find "$DIR/$(leg_dir dmg)" -name '*.dmg' | head -1)" ] ; then
        DMG="$(find "$DIR/$(leg_dir dmg)" -name '*.dmg' | head -1)"
        run_leg "dmg" bash -c '
        set -e
        # EXIT trap 在 set -e 下遇失败命令即中断且毒化最终 rc——detach 已卸卷是常态失败，须 || true 吞掉。
        T=$(mktemp -d "${TMPDIR:-/tmp}/bundler-consume.XXXXXX"); trap "hdiutil detach "$T/mnt" -force 2>/dev/null || true; rm -rf $T" EXIT
        hdiutil attach "$0" -nobrowse -readonly -mountpoint "$T/mnt"
        B=$(find "$T/mnt" -type d -name "*.app" | head -1)
        test -d "$B"
        cp -R "$B" "$T/app.app"
        hdiutil detach "$T/mnt" -force
        xattr -dr com.apple.quarantine "$T/app.app" 2>/dev/null || true
        chmod +x "$T/app.app/Contents/MacOS/BundlerMacDmgIntegrationFixture"
        # 静默失败曾吃掉整腿信息——显式打印 rc 与输出再判。
        set +e
        OUT=$("$T/app.app/Contents/MacOS/BundlerMacDmgIntegrationFixture" ci-marker 2>&1); rc=$?
        set -e
        echo "dmg-fixture rc=$rc out=$OUT"
        [ "$rc" -eq 0 ]
        echo "$OUT" | grep -q "BundlerMacDmgIntegrationFixture:ci-marker"
    ' "$DMG"
    else
        echo "[LEG-FAIL] dmg（产件目录无 .dmg）"; FAILED="$FAILED dmg-noformat"
    fi
fi

# .pkg：installer 真装到 /Applications → 原生启动 → forget+删
# 先清掉交换目录里的 .app 中间体：pkg-info 带 <relocate>，installer 见到同 bundle-id
# 的已存在 .app 会原地升级（relocation）而非落 /Applications——LaunchServices 索引
# 竞态导致落点漂移，删除同件保证 /Applications 确定性。
if leg_on pkg; then
    if [ -n "$(find "$DIR/$(leg_dir pkg)" -name '*.pkg' | head -1)" ] ; then
        find "$DIR/$(leg_dir pkg)" -type d -name '*.app' -exec rm -rf {} + 2>/dev/null || true
        PKG="$(find "$DIR/$(leg_dir pkg)" -name '*.pkg' | head -1)"
        run_leg "pkg" bash -c '
        set -e
        B="/Applications/Bundler Mac PKG Fixture.app"
        # installer/断言任何一步失败也要把 fixture 从 /Applications 清掉——
        # 清理命令自身失败不得毒化腿 rc（EXIT trap 同 set -e 交互，须 || true）。
        trap "sudo pkgutil --forget com.dotnetbundler.macpkgintegrationfixture 2>/dev/null || true; sudo rm -rf \"$B\" || true" EXIT
        sudo installer -pkg "$0" -target /
        test -d "$B"
        OUT=$("$B/Contents/MacOS/BundlerMacPkgIntegrationFixture" ci-marker)
        echo "$OUT" | grep -q "BundlerMacPkgIntegrationFixture:ci-marker"
        sudo pkgutil --forget com.dotnetbundler.macpkgintegrationfixture 2>/dev/null || true
        sudo rm -rf "$B"
    ' "$PKG"
    else
        echo "[LEG-FAIL] pkg（产件目录无 .pkg）"; FAILED="$FAILED pkg-noformat"
    fi
fi

# 归档件：默认模式解开直接跑；--archive-format-only 跨 OS 交换模式只做
# 格式级断言——逐份解包 + 单一顶层目录 + 主件在位 + runtimeconfig.json
# 文本标记可读，不执行异 RID 二进制。缺格式即判败（分 zip/targz 两腿）。
if leg_on archive; then
    if [ -z "$FMT_ONLY" ]; then
        if [ -n "$(find "$DIR/$(leg_dir archive)" -type f \( -name '*.zip' -o -name '*.tar.gz' \) | head -1)" ] ; then
            run_leg "archive" bash -c '
            set -e
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
        ' "$DIR/$(leg_dir archive)"
        else
            echo "[LEG-FAIL] archive（产件目录无 .zip/.tar.gz）"; FAILED="$FAILED archive-noformat"
        fi
    else
        if [ -n "$(find "$DIR/$(leg_dir archive)" -name '*.zip' | head -1)" ] ; then
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
        ' "$DIR/$(leg_dir archive)"
        else
            echo "[LEG-FAIL] archive-zip(xchg)（产件目录无 .zip）"; FAILED="$FAILED archive-zip-xchg-noformat"
        fi
        if [ -n "$(find "$DIR/$(leg_dir archive)" -name '*.tar.gz' | head -1)" ] ; then
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
        ' "$DIR/$(leg_dir archive)"
        else
            echo "[LEG-FAIL] archive-targz(xchg)（产件目录无 .tar.gz）"; FAILED="$FAILED archive-targz-xchg-noformat"
        fi
    fi
fi

# 零腿全绿防线：四条腿全被映射 "-"（如 --archive-format-only 叠加 archive=-）时
# 零断言却走到这里全绿——组合静默须判败。
[ "$RAN" -gt 0 ] || { echo "无任何腿执行（全 LEG-SKIP）" >&2; exit 1; }
if [ -n "$FAILED" ]; then echo "失败腿:$FAILED" >&2; exit 1; fi
echo "=== consume-macos 全绿 ==="
