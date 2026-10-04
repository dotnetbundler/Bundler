!macro WriteHookMarker name
  FileOpen $9 "$TEMP\DotNetBundler-${name}.txt" w
  FileWrite $9 "${name}"
  FileClose $9
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro WriteHookMarker "preinstall"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro WriteHookMarker "postinstall"
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  !insertmacro WriteHookMarker "preuninstall"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  !insertmacro WriteHookMarker "postuninstall"
!macroend
