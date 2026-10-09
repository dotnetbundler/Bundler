#!/usr/bin/env bash
# dbg-accept 驱动（一次性验收）：只跑本轮新增腿，不跑全量——快速迭代用。
# ubuntu: appimage-desktop + apk-index 扩展腿；macos-26: Rosetta + quarantine。
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"; OUT="$ROOT/artifacts/ci"; mkdir -p "$OUT"
ARCH="$(uname -m)"; OS="$(uname -s)"
say(){ echo "=== $*"; }
FAILED_LEGS=""
run_leg(){ local name="$1"; shift; if "$@"; then echo "[LEG-PASS] $name"; else echo "[LEG-FAIL] $name"; FAILED_LEGS="$FAILED_LEGS $name"; fi; }
find1(){ find "$OUT/$1" -type f -name "$2" | head -1; }
finddir(){ find "$OUT/$1" -type d -name "$2" | head -1; }
FIX="tests/Bundler.IntegrationTests/Fixtures"
produce(){ dotnet publish "$FIX/$1" -c Release -r "$2" -p:BundlerIntegrationOutput="$OUT/$3" --nologo; }
SP="tests/Special"

say "构建"
dotnet build Bundler.slnx -c Release --nologo
dotnet pack Bundler.slnx -c Release -o artifacts/packages --nologo

case "$OS" in
Linux)
    say "fixture 产包"
    produce "AppImage/BundlerAppImageIntegrationFixture.csproj"   "linux-x64"      "appimage"
    produce "AlpineApk/BundlerAlpineApkIntegrationFixture.csproj" "linux-musl-x64" "apk"
    run_leg "apk-index(+repo add)" bash "$SP/linux/apk-index.sh" --apk "$(find1 apk '*.apk')"
    run_leg "appimage-desktop"   bash "$SP/linux/appimage-desktop.sh" --appimage "$(find1 appimage '*.AppImage')"
    ;;
Darwin)
    say "osx-x64 fixture 产包（Rosetta 腿用）"
    produce "MacApp/BundlerMacAppIntegrationFixture.csproj" "osx-x64" "app-x64"
    produce "MacDmg/BundlerMacDmgIntegrationFixture.csproj" "osx-x64" "dmg-x64"
    say "装 Rosetta"
    if [ "$ARCH" = "arm64" ]; then
        /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null \
            || softwareupdate --install-rosetta --agree-to-license
    fi
    /usr/bin/arch -x86_64 /usr/bin/true && echo "rosetta 可用"
    run_leg "intel-x64(rosetta)" bash "$SP/mac/intel-x64.sh" \
        --app "$(finddir app-x64 '*.app')" --dmg "$(find1 dmg-x64 '*.dmg')"
    say "quarantine 模拟腿"
    produce "MacApp/BundlerMacAppIntegrationFixture.csproj" "osx-$([ "$ARCH" = "arm64" ] && echo arm64 || echo x64)" "app"
    run_leg "quarantine" bash "$SP/mac/quarantine.sh" --app "$(finddir app '*.app')"
    ;;
esac
if [ -n "$FAILED_LEGS" ]; then echo "失败腿:$FAILED_LEGS" >&2; exit 1; fi
say "全绿"
