#!/usr/bin/env bash
# 定向测试腿驱动（POSIX）：run-suite.sh <unit|api|integration|localpackages|all>
# 跑法与 verify-posix.sh §2 同款：直跑测试 dll（MTP+xUnit3，`dotnet test` 发现不到用例），
# IntegrationTests 逐类跑并落 artifacts/ci/test-progress.log 指认在途类；
# -longRunning 300 打印挂起用例名。
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
OUT="$ROOT/artifacts/ci"; mkdir -p "$OUT"
SUITE="${1:?usage: run-suite.sh <unit|api|integration|localpackages|all>}"

projects_for() {
    case "$1" in
        unit)          echo "Bundler.Tests" ;;
        api)           echo "Bundler.ApiTests" ;;
        integration)   echo "Bundler.IntegrationTests" ;;
        localpackages) echo "Bundler.LocalPackagesTests" ;;
        all)           echo "Bundler.Tests Bundler.ApiTests Bundler.IntegrationTests Bundler.LocalPackagesTests" ;;
        *) echo "unknown suite: $1" >&2; exit 2 ;;
    esac
}
PROJECTS="$(projects_for "$SUITE")"

# LocalPackagesTests 消费本地 nupkg——先打仓根包再跑（tests/README 前置条件）。
case " $PROJECTS " in
    *" Bundler.LocalPackagesTests "*)
        dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo ;;
esac

for P in $PROJECTS; do
    DLL="tests/$P/bin/Release/net10.0/$P.dll"
    if [ "$P" = "Bundler.IntegrationTests" ]; then
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
