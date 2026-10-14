; 创建仅供仓库 Fixture 使用的内部标记，使 commit 在完成原子目录重命名后保留
; `.committed` 快照；下一次启动应只清理它，不能回滚已经提交的安装。
!macro NSIS_HOOK_POSTINSTALL
  FileOpen $9 "$TransactionDirectory\.dotnet-bundler-test-retain-committed" w
  FileWrite $9 "retain"
  FileClose $9
!macroend
