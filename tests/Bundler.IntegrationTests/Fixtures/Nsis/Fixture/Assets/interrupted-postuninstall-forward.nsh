; 首次 post-uninstall 等待外部测试终止进程树；恢复时跳过等待。
!macro NSIS_HOOK_POSTUNINSTALL
  IfFileExists "$TEMP\DotNetBundler-interrupted-postuninstall-forward.once" recovered
  FileOpen $9 "$TEMP\DotNetBundler-interrupted-postuninstall-forward.once" w
  FileWrite $9 "interrupt-once"
  FileClose $9
  Sleep 30000
  recovered:
!macroend
