# WIN-MSI-5 x86 版本映射与降级策略。
# 测试体已收编进 tests/Bundler.IntegrationTests（xUnit v3）；本脚本只是薄入口，
# 保留文件名与参数口径供既有提示词/文档引用。内容断言、状态清理、门禁全在 C# 侧。
param(
    [string]$Configuration = 'Release',
    [switch]$ConfirmDisposableVm,
    [switch]$ConfirmLocalInstall
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'\'..'))
if ($ConfirmLocalInstall.IsPresent -or $ConfirmDisposableVm.IsPresent) {
    $env:BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL = '1'
}
dotnet test (Join-Path $repository 'tests' 'Bundler.IntegrationTests' 'Bundler.IntegrationTests.csproj') `
    -c $Configuration -- --filter-class MsiIntegrationTests --filter-method "MsiIntegrationTests.X86VersionMappingAndDowngradePolicy"
exit $LASTEXITCODE
