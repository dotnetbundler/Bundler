# WIN-MSI 公共样例表级契约（HelloMsiApp 三变体，不安装）。
# 测试体已收编进 tests/Bundler.IntegrationTests；本脚本只是薄入口。
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'\'..'))
dotnet test (Join-Path $repository 'tests' 'Bundler.IntegrationTests' 'Bundler.IntegrationTests.csproj') `
    -c $Configuration -- --filter-class MsiIntegrationTests --filter-method PublicSampleMsiTableContract
exit $LASTEXITCODE
