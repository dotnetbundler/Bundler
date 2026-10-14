; 仅供仓库集成测试在快照完成后注入一次性激活失败。
!macro DOTNET_BUNDLER_TEST_BEFORE_TRANSACTION_ACTIVATE
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-fail-next-activation" w
  FileWrite $9 "fail-once"
  FileClose $9
!macroend
