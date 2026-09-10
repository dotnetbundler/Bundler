Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!define PRODUCT_NAME "{{product_name}}"
!define PRODUCT_VERSION "{{version}}"
!define PRODUCT_NUMERIC_VERSION "{{numeric_version}}"
!define PRODUCT_PUBLISHER "{{publisher}}"
!define PRODUCT_ID "{{identifier}}"
!define MAIN_EXECUTABLE "{{main_executable}}"
!define PROCESS_NAME "{{process_name}}"
!define INSTALL_FOLDER "{{install_folder}}"
!define INPUT_GLOB "{{input_glob}}"
!define OUTPUT_FILE "{{output_file}}"
!define ESTIMATED_SIZE "{{estimated_size}}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"
!define INSTALL_MARKER ".dotnet-bundler-${PRODUCT_ID}"

Var CreateDesktopShortcut
Var CreateStartMenuShortcut
Var DesktopShortcutCheckbox
Var StartMenuShortcutCheckbox
Var DeleteAppData
Var DeleteAppDataCheckbox

Name "${PRODUCT_NAME}"
BrandingText "${PRODUCT_PUBLISHER}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${INSTALL_FOLDER}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user

VIProductVersion "${PRODUCT_NUMERIC_VERSION}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} Installer"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN "$INSTDIR\${MAIN_EXECUTABLE}"
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "Software\${PRODUCT_ID}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "Installer Language"

!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
Page custom ShortcutOptionsPage ShortcutOptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.AppDataOptionsPage un.AppDataOptionsLeave
!insertmacro MUI_UNPAGE_INSTFILES

{{language_macros}}
!insertmacro MUI_RESERVEFILE_LANGDLL
{{language_files}}

Function .onInit
  StrCpy $CreateDesktopShortcut 1
  StrCpy $CreateStartMenuShortcut 1
{{display_language_selector}}
FunctionEnd

Function un.onInit
  StrCpy $DeleteAppData 0
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd

Function ValidateInstallDirectory
  IfFileExists "$INSTDIR\${INSTALL_MARKER}" directory_valid
  FindFirst $0 $1 "$INSTDIR\*"
  directory_scan:
    StrCmp $1 "" directory_empty
    StrCmp $1 "." directory_next
    StrCmp $1 ".." directory_next
    FindClose $0
    MessageBox MB_ICONEXCLAMATION|MB_YESNO "$(NonEmptyDirectoryWarning)" IDYES directory_valid
    Abort
  directory_next:
    FindNext $0 $1
    Goto directory_scan
  directory_empty:
    FindClose $0
  directory_valid:
FunctionEnd

Function ShortcutOptionsPage
  !insertmacro MUI_HEADER_TEXT "$(ShortcutPageTitle)" "$(ShortcutPageSubtitle)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 20u 100% 12u "$(DesktopShortcutLabel)"
  Pop $DesktopShortcutCheckbox
  ${NSD_Check} $DesktopShortcutCheckbox
  ${NSD_CreateCheckbox} 0 48u 100% 12u "$(StartMenuShortcutLabel)"
  Pop $StartMenuShortcutCheckbox
  ${NSD_Check} $StartMenuShortcutCheckbox
  nsDialogs::Show
FunctionEnd

Function ShortcutOptionsLeave
  ${NSD_GetState} $DesktopShortcutCheckbox $CreateDesktopShortcut
  ${NSD_GetState} $StartMenuShortcutCheckbox $CreateStartMenuShortcut
FunctionEnd

Function un.AppDataOptionsPage
  !insertmacro MUI_HEADER_TEXT "$(AppDataPageTitle)" "$(AppDataPageSubtitle)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 30u 100% 24u "$(DeleteAppDataLabel)"
  Pop $DeleteAppDataCheckbox
  nsDialogs::Show
FunctionEnd

Function un.AppDataOptionsLeave
  ${NSD_GetState} $DeleteAppDataCheckbox $DeleteAppData
FunctionEnd

Function EnsureAppClosed
  nsExec::ExecToStack 'tasklist.exe /NH /FI "IMAGENAME eq ${PROCESS_NAME}"'
  Pop $0
  Pop $1
  ${StrStr} $2 "$1" "${PROCESS_NAME}"
  StrCmp $2 "" app_closed
  IfSilent close_app 0
  MessageBox MB_ICONEXCLAMATION|MB_OKCANCEL "$(AppRunningPrompt)" IDOK close_app IDCANCEL cancel_close
  close_app:
    nsExec::ExecToStack 'taskkill.exe /F /T /IM "${PROCESS_NAME}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" app_closed
    MessageBox MB_ICONSTOP "$(AppCloseFailed)"
    Abort
  cancel_close:
    Abort
  app_closed:
FunctionEnd

Function un.EnsureAppClosed
  nsExec::ExecToStack 'tasklist.exe /NH /FI "IMAGENAME eq ${PROCESS_NAME}"'
  Pop $0
  Pop $1
  ${UnStrStr} $2 "$1" "${PROCESS_NAME}"
  StrCmp $2 "" un_app_closed
  IfSilent un_close_app 0
  MessageBox MB_ICONEXCLAMATION|MB_OKCANCEL "$(AppRunningPrompt)" IDOK un_close_app IDCANCEL un_cancel_close
  un_close_app:
    nsExec::ExecToStack 'taskkill.exe /F /T /IM "${PROCESS_NAME}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" un_app_closed
    MessageBox MB_ICONSTOP "$(AppCloseFailed)"
    Abort
  un_cancel_close:
    Abort
  un_app_closed:
FunctionEnd

Section "Install" MainSection
  SetShellVarContext current
  Call EnsureAppClosed
  SetOutPath "$INSTDIR"
  File /r "${INPUT_GLOB}"

  FileOpen $0 "$INSTDIR\${INSTALL_MARKER}" w
  FileWrite $0 "${PRODUCT_ID}"
  FileClose $0

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ${If} $CreateStartMenuShortcut == 1
    CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
    CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXECUTABLE}"
  ${EndIf}
  ${If} $CreateDesktopShortcut == 1
    CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXECUTABLE}"
  ${EndIf}

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${MAIN_EXECUTABLE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${ESTIMATED_SIZE}
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Call un.EnsureAppClosed
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey /ifempty HKCU "Software\${PRODUCT_ID}"
  ${If} $DeleteAppData == 1
    RMDir /r "$APPDATA\${PRODUCT_ID}"
    RMDir /r "$LOCALAPPDATA\${PRODUCT_ID}"
    RMDir /r /REBOOTOK "$INSTDIR"
  ${Else}
{{uninstall_payload}}
    Delete "$INSTDIR\${INSTALL_MARKER}"
    Delete /REBOOTOK "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}
SectionEnd
