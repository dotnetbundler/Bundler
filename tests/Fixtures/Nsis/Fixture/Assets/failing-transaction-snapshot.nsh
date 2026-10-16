; 仅供仓库集成测试在第一个事务快照写入时注入一次性 I/O 失败。
!macro DOTNET_BUNDLER_TEST_AFTER_TRANSACTION_BEGIN
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-fail-next-snapshot" w
  FileWrite $9 "fail-once"
  FileClose $9
!macroend
