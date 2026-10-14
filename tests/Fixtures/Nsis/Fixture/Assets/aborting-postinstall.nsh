; 通知外部测试进程已经到达 post-install，然后等待整棵安装器进程树被终止。
!macro NSIS_HOOK_POSTINSTALL
  FileOpen $0 "$TEMP\DotNetBundler-interrupted-postinstall.txt" w
  FileWrite $0 "ready"
  FileClose $0
  Sleep 30000
!macroend
