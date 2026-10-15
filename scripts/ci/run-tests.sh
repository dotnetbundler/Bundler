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
