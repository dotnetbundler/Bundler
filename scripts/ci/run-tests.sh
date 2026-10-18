#!/usr/bin/env bash
# 测试腿唯一驱动（POSIX）：run-tests.sh <unit|api|integration|package|all>
# 测试程序集自宿主（MTP+xUnit3）——直跑 dll 而非 dotnet test，以逐类调度、
# 落 artifacts/ci/test-progress.log 指认在途类；-longRunning 300 打印挂起用例名。
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
OUT="$ROOT/artifacts/ci"; mkdir -p "$OUT"
SUITE="${1:?usage: run-tests.sh <unit|api|integration|package|all>}"

projects_for() {
    case "$1" in
        unit)        echo "Unit/Bundler.UnitTests" ;;
        api)         echo "Api/Bundler.ApiTests" ;;
        integration) echo "Integration/Bundler.IntegrationTests" ;;
        package)     echo "Package/Bundler.PackageTests" ;;
        all)         echo "Unit/Bundler.UnitTests Api/Bundler.ApiTests Integration/Bundler.IntegrationTests Package/Bundler.PackageTests" ;;
        *) echo "unknown suite: $1" >&2; exit 2 ;;
    esac
}
PROJECTS="$(projects_for "$SUITE")"

# PackageTests 消费本地 nupkg——先打仓根 src 包再跑（tests/README 前置条件）。
# 只打 src/*/*.csproj：连测试工程一起 pack 会向运行中进程的 bin/ 写文件。
case " $PROJECTS " in
    *" Package/Bundler.PackageTests "*)
        SRC_PROJECTS=(src/*/*.csproj)
        if [ ${#SRC_PROJECTS[@]} -eq 0 ] || [ ! -e "${SRC_PROJECTS[0]}" ]; then
            echo "src/*/*.csproj 零命中——pack 落空" >&2; exit 1
        fi
        for P in "${SRC_PROJECTS[@]}"; do
            dotnet pack "$P" -c Release -o artifacts/packages --nologo
        done ;;
esac

# 挂死插桩：BUNDLER_TOOL_TRACE 指文件时后台 tail 进本 job 日志——job 被 timeout 硬杀
# 也能从日志看到最后一条在途 spawn（文件自身不进工件，日志即证据）。
TRACE_TAIL=""
if [ -n "${BUNDLER_TOOL_TRACE:-}" ]; then
    mkdir -p "$(dirname "$BUNDLER_TOOL_TRACE")"; : > "$BUNDLER_TOOL_TRACE"
    tail -n +1 -f "$BUNDLER_TOOL_TRACE" &
    TRACE_TAIL=$!
fi

# 进程快照采样：每 60s 一份 uptime+过滤后进程清单+hdiutil info 落 process-snapshots.log
# 并 tail 进 job 日志——挂死复现时 trace 指认最后在途 spawn，快照指认其进程树/状态。
# ps 只留头行+非常驻状态（R/D/U/T/Z 首字符——S/I 长眠态滤掉）+spawn 面命令白名单：
# 全量 ~300 行/份裁到数十行。白名单含已知嫌疑守护进程与 IPC 对端（securityd/amfid/
# syspolicyd/diskimages/diskarbitrationd/trustd/installd/lsd）、SLA 点击驱动 osascript/
# open、提权/包管 sudo/pkgutil/dpkg/rpm/docker 面与宿主壳 bash/tail——宁宽勿漏。
SNAP="$OUT/process-snapshots.log"; : > "$SNAP"
(
    while :; do
        {
            echo "===== $(date -u +%FT%TZ) ====="
            uptime || true
            ps -axww -o pid,ppid,stat,etime,command 2>/dev/null \
                | awk 'NR==1 || $3 ~ /^[RDUTZ]/ || /dotnet|Bundler|hdiutil|codesign|security|pkgbuild|productbuild|productsign|pkgutil|installd|makensis|notarytool|ditto|xar |lipo|otool|spctl|osascript|syspolicyd|amfid|trustd|diskimages|diskarbitrationd|lsd|sudo|docker|containerd|dpkg|rpm|apk |abuild|zip |tar |gzip|sleep|bash|tail|open |[ /]sh /' \
                || ps auxww || true
            command -v hdiutil >/dev/null 2>&1 && hdiutil info || true
            echo
        } >> "$SNAP"
        sleep 60
    done
) &
SNAP_LOOP=$!
tail -n +1 -f "$SNAP" &
SNAP_TAIL=$!
trap 'kill $TRACE_TAIL $SNAP_LOOP $SNAP_TAIL 2>/dev/null || true' EXIT

for P in $PROJECTS; do
    LAYER="${P%%/*}"; NAME="${P##*/}"
    DLL="tests/$LAYER/bin/Release/net10.0/$NAME.dll"
    [ -f "$DLL" ] || { echo "测试程序集不存在：${DLL}（先 dotnet build）" >&2; exit 1; }
    if [ "$NAME" = "Bundler.IntegrationTests" ]; then
        # mapfile 是 bash>=4 内建（macOS runner 自带 bash 3.2.57）——while 读行代替。
        # -list classes 本身失败也要判败：进程替换会丢退出码，先落变量再查。
        LIST_OUT="$(dotnet "$DLL" -list classes)" || { echo "-list classes 退出码非零，判失败" >&2; exit 1; }
        CLASSES=()
        while IFS= read -r line; do
            [[ "$line" =~ ^[[:alnum:]_][[:alnum:]_.]*$ ]] && CLASSES+=("$line")
        done <<< "$LIST_OUT"
        if [ ${#CLASSES[@]} -eq 0 ]; then
            echo "-list classes 无输出：集成测试可能整段蒸发，判失败" >&2; exit 1
        fi
        for C in "${CLASSES[@]}"; do
            echo "[$(date -u +%FT%TZ)] BEGIN $C" >> "$OUT/test-progress.log"
            echo "[CLASS-BEGIN] $C"
            if ! dotnet "$DLL" -class "$C" -longRunning 300; then
                echo "[CLASS-FAIL] $C"; exit 1
            fi
            echo "[$(date -u +%FT%TZ)] END   $C rc=0" >> "$OUT/test-progress.log"
        done
    else
        dotnet "$DLL" -longRunning 300
    fi
done
