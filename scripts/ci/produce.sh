#!/usr/bin/env bash
# fixture 产件 + sha256 清单（POSIX）：produce.sh [输出目录]
# verify 流水线联合交换的生产侧：产物+SHA256SUMS 打成上传 artifact，
# 异机消费腿按清单验同一件再真装真跑（消费机不见构建树）。
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
OUT="${1:-$ROOT/artifacts/ci/produce}"; mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
FIX="$ROOT/tests/Fixtures"
ARCH="$(uname -m)"; OS="$(uname -s)"

# fixture 经 Bundler.ProjectReference.targets 走源内 buildTransitive——
# UsingTask 需任务程序集先在 bin/Release，否则报 MSB4062。
dotnet build src/Bundler.MSBuild/Bundler.MSBuild.csproj -c Release --nologo

produce(){ # $1=fixture csproj 相对 $FIX 路径 $2=rid $3=out-subdir
    dotnet publish "$FIX/$1" -c Release -r "$2" \
        -p:BundlerIntegrationOutput="$OUT/$3" --nologo
}
produce_archive(){ # archive fixture 不跑 RID 发布——固定宿主任一 rid 产归档件
    dotnet publish "$FIX/Archive/BundlerArchiveIntegrationFixture.csproj" -c Release -r "$1" \
        -p:BundlerIntegrationOutput="$OUT/archive" --nologo
}

case "$OS" in
Linux)
    RID="linux-x64"; [ "$ARCH" = "aarch64" ] && RID="linux-arm64"
    produce "Deb/BundlerDebIntegrationFixture.csproj"         "$RID" "deb"
    produce "Rpm/BundlerRpmIntegrationFixture.csproj"         "$RID" "rpm"
    produce "AppImage/BundlerAppImageIntegrationFixture.csproj" "$RID" "appimage"
    produce "AlpineApk/BundlerAlpineApkIntegrationFixture.csproj" \
        "linux-musl-$( [ "$ARCH" = "aarch64" ] && echo arm64 || echo x64 )" "apk"
    produce_archive "$RID"
    ;;
Darwin)
    RID="osx-x64"; [ "$ARCH" = "arm64" ] && RID="osx-arm64"
    produce "MacApp/BundlerMacAppIntegrationFixture.csproj" "$RID" "app"
    produce "MacDmg/BundlerMacDmgIntegrationFixture.csproj" "$RID" "dmg"
    produce "MacPkg/BundlerMacPkgIntegrationFixture.csproj" "$RID" "pkg"
    produce_archive "$RID"
    if [ "$ARCH" = "arm64" ]; then
        # Rosetta 腿：osx-x64 件供 manual-auto 的 intel-x64 验收（arm64+Rosetta 实跑）。
        produce "MacApp/BundlerMacAppIntegrationFixture.csproj" "osx-x64" "app-x64"
        produce "MacDmg/BundlerMacDmgIntegrationFixture.csproj" "osx-x64" "dmg-x64"
    fi
    ;;
*) echo "produce.sh 不支持宿主 $OS" >&2; exit 1 ;;
esac

( cd "$OUT" && find . -type f ! -name SHA256SUMS -exec shasum -a 256 {} + | sort > SHA256SUMS )
echo "=== 产件清单 ==="; cat "$OUT/SHA256SUMS" | head -40
