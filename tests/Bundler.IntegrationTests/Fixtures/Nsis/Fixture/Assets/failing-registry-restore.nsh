; 在新状态已写入后触发回滚，并使第一次注册表恢复失败。
!macro NSIS_HOOK_POSTINSTALL
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-fail-next-registry-restore" w
  FileWrite $9 "fail-once"
  FileClose $9
  SetErrors
!macroend
