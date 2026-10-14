; 在新状态已写入后触发回滚，并使恢复完成后的 journal 清理失败一次。
!macro NSIS_HOOK_POSTINSTALL
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-fail-next-journal-cleanup" w
  FileWrite $9 "fail-once"
  FileClose $9
  SetErrors
!macroend
