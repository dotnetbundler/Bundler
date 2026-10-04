; 在新状态已写入后触发回滚，并使第一次载荷恢复失败。
!macro NSIS_HOOK_POSTINSTALL
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-fail-next-payload-restore" w
  FileWrite $9 "fail-once"
  FileClose $9
  SetErrors
!macroend
