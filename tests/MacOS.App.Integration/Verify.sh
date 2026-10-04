#!/usr/bin/env bash
# macOS .app集成测试入口。
# 测试体已收编进 tests/Bundler.IntegrationTests（MacAppIntegrationTests，xUnit v3）；本脚本只是薄入口，
# 保留路径与调用口径供既有提示词/文档引用。宿主门禁、断言、清理全在 C# 侧。
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
dotnet test "$repo_root/tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj" \
    -c Release -- --filter-class MacAppIntegrationTests
