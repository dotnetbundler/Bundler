; 生命周期 Hook 演示：每个阶段在临时目录写入一个标记文件。
!macro WriteHelloBundlerAppHookMarker name
  FileOpen $9 "$TEMP\HelloBundlerApp-hook-${name}.txt" w
  FileWrite $9 "${name}"
  FileClose $9
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro WriteHelloBundlerAppHookMarker "preinstall"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro WriteHelloBundlerAppHookMarker "postinstall"
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  !insertmacro WriteHelloBundlerAppHookMarker "preuninstall"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  !insertmacro WriteHelloBundlerAppHookMarker "postuninstall"
!macroend
