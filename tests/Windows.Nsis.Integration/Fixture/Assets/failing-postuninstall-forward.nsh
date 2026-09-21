; 首次 post-uninstall 返回失败，恢复卸载器重试时成功。
!macro NSIS_HOOK_POSTUNINSTALL
  IfFileExists "$TEMP\DotNetBundler-failing-postuninstall-forward.once" recovered
  FileOpen $9 "$TEMP\DotNetBundler-failing-postuninstall-forward.once" w
  FileWrite $9 "fail-once"
  FileClose $9
  SetErrors
  recovered:
!macroend
