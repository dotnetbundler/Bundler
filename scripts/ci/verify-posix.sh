#!/usr/bin/env bash
# 发布前验证驱动（POSIX：ubuntu x64/arm + macos intel/arm64）。
# 段序：构建 → 全量 dotnet test → fixture 产包 → Special 自动腿。
# 残留纪律：产包与证据落 artifacts/ci/（job 结束由 runner 回收），工作区不留临时目录。
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
OUT="$ROOT/artifacts/ci"; mkdir -p "$OUT"
ARCH="$(uname -m)"; OS="$(uname -s)"
say(){ echo "=== $*"; }

say "1/4 构建+打包"
dotnet build Bundler.slnx -c Release --nologo
dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo

say "2/4 全量测试（四工程）"
# 测试程序集自宿主运行（MTP+xUnit3）：直跑 dll，输出即进度。
# IntegrationTests 逐类跑并落进度文件：挂起/被砍时 artifacts 能指认在途类；
# -longRunning 超时打印挂起用例名。
for P in Bundler.Tests Bundler.ApiTests Bundler.IntegrationTests Bundler.LocalPackagesTests; do
    DLL="tests/$P/bin/Release/net10.0/$P.dll"
    if [ "$P" = "Bundler.IntegrationTests" ]; then
        mapfile -t CLASSES < <(dotnet "$DLL" -list classes | grep -E '^\w[\w.]*$' || true)
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

say "3/4 fixture 产包（喂 Special 腿）"
FIX="tests/Bundler.IntegrationTests/Fixtures"
produce(){ # $1=fixture csproj $2=rid $3=out-subdir
    dotnet publish "$FIX/$1" -c Release -r "$2" -p:BundlerIntegrationOutput="$OUT/$3" --nologo
}
case "$OS" in
Linux)
    RID="linux-x64"; [ "$ARCH" = "aarch64" ] && RID="linux-arm64"
    produce "Deb/BundlerDebIntegrationFixture.csproj"                 "$RID" "deb"
    produce "Rpm/BundlerRpmIntegrationFixture.csproj"                 "$RID" "rpm"
    produce "AppImage/BundlerAppImageIntegrationFixture.csproj"       "$RID" "appimage"
    produce "AlpineApk/BundlerAlpineApkIntegrationFixture.csproj"     "linux-musl-$( [ "$ARCH" = "aarch64" ] && echo arm64 || echo x64 )" "apk"
    ;;
Darwin)
    RID="osx-x64"; [ "$ARCH" = "arm64" ] && RID="osx-arm64"
    produce "MacApp/BundlerMacAppIntegrationFixture.csproj"           "$RID" "app"
    produce "MacDmg/BundlerMacDmgIntegrationFixture.csproj"           "$RID" "dmg"
    produce "MacPkg/BundlerMacPkgIntegrationFixture.csproj"           "$RID" "pkg"
    ;;
esac

say "4/4 Special 自动腿（CI 环境可跑者；凭证/公网/人工类仍排除）"
SP="tests/Special"
run_leg(){ # 腿失败不连锁——逐条收集，末段统一判
    local name="$1"; shift
    if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED_LEGS="$FAILED_LEGS $name"; fi
}
FAILED_LEGS=""
find1(){ find "$OUT/$1" -type f -name "$2" | head -1; }
finddir(){ find "$OUT/$1" -type d -name "$2" | head -1; }

if [ "$OS" = "Linux" ]; then
    run_leg "gpg-production(self-contained)" bash "$SP/linux/gpg-production.sh" --self-contained
    run_leg "apk-index"                    bash "$SP/linux/apk-index.sh" --apk "$(find1 apk '*.apk')"
    if [ "$ARCH" = "aarch64" ]; then
        run_leg "arm64-real-hw"            bash "$SP/linux/arm64-real-hw.sh" \
            --deb "$(find1 deb '*.deb')" --rpm "$(find1 rpm '*.rpm')" \
            --appimage "$(find1 appimage '*.AppImage')"
    fi
elif [ "$OS" = "Darwin" ]; then
    run_leg "clean-host-matrix"            bash "$SP/mac/clean-host-matrix.sh" \
        --app "$(finddir app '*.app')" --dmg "$(find1 dmg '*.dmg')" --pkg "$(find1 pkg '*.pkg')"
    if [ "$ARCH" = "x86_64" ]; then
        run_leg "intel-x64"                bash "$SP/mac/intel-x64.sh" \
            --app "$(finddir app '*.app')" --dmg "$(find1 dmg '*.dmg')"
    else
        # Rosetta 腿：osx-x64 件在 arm64+Rosetta 下实跑（Intel Mac 行的另一半）
        produce "MacApp/BundlerMacAppIntegrationFixture.csproj"       "osx-x64" "app-x64"
        produce "MacDmg/BundlerMacDmgIntegrationFixture.csproj"       "osx-x64" "dmg-x64"
        /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null \
            || softwareupdate --install-rosetta --agree-to-license
        run_leg "intel-x64(rosetta)" bash "$SP/mac/intel-x64.sh" \
            --app "$(finddir app-x64 '*.app')" --dmg "$(find1 dmg-x64 '*.dmg')"
    fi
fi

if [ -n "$FAILED_LEGS" ]; then
    echo "失败腿:$FAILED_LEGS" >&2; exit 1
fi
say "全绿"
