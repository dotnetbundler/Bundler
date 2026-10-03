# Windows NSIS 集成测试入口。
# 测试体已收编进 tests/Bundler.IntegrationTests（NsisIntegrationTests，xUnit v3）；
# 本脚本只是薄入口。原脚本无同意开关（面向一次性测试宿主）——跑本脚本即同意本机真装。
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..', '..'))
$env:BUNDLER_INTEGRATION_ALLOW_LOCAL_INSTALL = '1'
dotnet test ([IO.Path]::Combine($repository, 'tests', 'Bundler.IntegrationTests', 'Bundler.IntegrationTests.csproj')) `
    -c $Configuration -- --filter-class NsisIntegrationTests
exit $LASTEXITCODE
