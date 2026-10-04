; 生命周期 Hook 演示：每个阶段在临时目录写入一个标记文件。
!macro WriteHelloBundledAppHookMarker name
  FileOpen $9 "$TEMP\HelloBundledApp-hook-${name}.txt" w
  FileWrite $9 "${name}"
  FileClose $9
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro WriteHelloBundledAppHookMarker "preinstall"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro WriteHelloBundledAppHookMarker "postinstall"
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  !insertmacro WriteHelloBundledAppHookMarker "preuninstall"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  !insertmacro WriteHelloBundledAppHookMarker "postuninstall"
!macroend
