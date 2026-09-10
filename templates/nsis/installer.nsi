Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "StrFunc.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
${StrStr}
${UnStrStr}

!define PRODUCT_NAME "{{product_name}}"
!define PRODUCT_VERSION "{{version}}"
!define PRODUCT_NUMERIC_VERSION "{{numeric_version}}"
!define PRODUCT_PUBLISHER "{{publisher}}"
!define PRODUCT_DESCRIPTION "{{description}}"
!define PRODUCT_HOMEPAGE "{{homepage}}"
!define PRODUCT_COPYRIGHT "{{copyright}}"
!define PRODUCT_ID "{{identifier}}"
!define MAIN_EXECUTABLE "{{main_executable}}"
!define PROCESS_NAME "{{process_name}}"
!define INSTALL_FOLDER "{{install_folder}}"
!define INSTALL_MODE "{{install_mode}}"
!define TARGET_ARCHITECTURE "{{target_architecture}}"
!define INPUT_GLOB "{{input_glob}}"
!define OUTPUT_FILE "{{output_file}}"
!define ESTIMATED_SIZE "{{estimated_size}}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"
!define INSTALL_MARKER ".dotnet-bundler-${PRODUCT_ID}"
{{installer_icon_directives}}

Var CreateDesktopShortcut
Var CreateStartMenuShortcut
Var DesktopShortcutCheckbox
Var StartMenuShortcutCheckbox
Var DeleteAppData
Var DeleteAppDataCheckbox

Name "${PRODUCT_NAME}"
BrandingText "${PRODUCT_PUBLISHER}"
OutFile "${OUTPUT_FILE}"
InstallDir "placeholder\${INSTALL_FOLDER}"

!if "${INSTALL_MODE}" == "currentUser"
  RequestExecutionLevel user
!else if "${INSTALL_MODE}" == "perMachine"
  RequestExecutionLevel admin
!else if "${INSTALL_MODE}" == "both"
  !define MULTIUSER_MUI
  !define MULTIUSER_INSTALLMODE_INSTDIR "${INSTALL_FOLDER}"
  !define MULTIUSER_INSTALLMODE_COMMANDLINE
  !define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_KEY "${UNINSTALL_KEY}"
  !define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME "InstallLocation"
  !define MULTIUSER_INSTALLMODEPAGE_SHOWUSERNAME
  !define MULTIUSER_EXECUTIONLEVEL Highest
  !if "${TARGET_ARCHITECTURE}" == "x64"
    !define MULTIUSER_USE_PROGRAMFILES64
  !else if "${TARGET_ARCHITECTURE}" == "arm64"
    !define MULTIUSER_USE_PROGRAMFILES64
  !endif
  !include "MultiUser.nsh"
!endif

VIProductVersion "${PRODUCT_NUMERIC_VERSION}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT_DESCRIPTION}"
VIAddVersionKey "LegalCopyright" "${PRODUCT_COPYRIGHT}"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN "$INSTDIR\${MAIN_EXECUTABLE}"
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "Software\${PRODUCT_ID}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "Installer Language"

!insertmacro MUI_PAGE_WELCOME
{{license_page}}
!if "${INSTALL_MODE}" == "both"
  !insertmacro MULTIUSER_PAGE_INSTALLMODE
!endif
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

!macro SetInstallContext
  !if "${INSTALL_MODE}" == "currentUser"
    SetShellVarContext current
  !else if "${INSTALL_MODE}" == "perMachine"
    SetShellVarContext all
  !endif
  !if "${TARGET_ARCHITECTURE}" == "x64"
    SetRegView 64
  !else if "${TARGET_ARCHITECTURE}" == "arm64"
    SetRegView 64
  !else
    SetRegView 32
  !endif
!macroend

Function .onInit
  StrCpy $CreateDesktopShortcut 1
  StrCpy $CreateStartMenuShortcut 1
{{display_language_selector}}
  !insertmacro SetInstallContext
  !if "${INSTALL_MODE}" == "both"
    !insertmacro MULTIUSER_INIT
  !else
    Call SetDefaultInstallDirectory
  !endif
FunctionEnd

Function un.onInit
  StrCpy $DeleteAppData 0
  ${GetOptions} $CMDLINE "/DELETEAPPDATA" $0
  ${IfNot} ${Errors}
    StrCpy $DeleteAppData 1
  ${EndIf}
  !insertmacro MUI_UNGETLANGUAGE
  !insertmacro SetInstallContext
  !if "${INSTALL_MODE}" == "both"
    !insertmacro MULTIUSER_UNINIT
  !endif
FunctionEnd

Function SetDefaultInstallDirectory
  ${If} $INSTDIR == "placeholder\${INSTALL_FOLDER}"
    !if "${INSTALL_MODE}" == "currentUser"
      StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${INSTALL_FOLDER}"
    !else if "${INSTALL_MODE}" == "perMachine"
      ${If} ${RunningX64}
        StrCpy $INSTDIR "$PROGRAMFILES64\${INSTALL_FOLDER}"
      ${Else}
        StrCpy $INSTDIR "$PROGRAMFILES\${INSTALL_FOLDER}"
      ${EndIf}
    !endif
    ReadRegStr $0 SHCTX "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 != ""
      StrCpy $INSTDIR $0
    ${EndIf}
  ${EndIf}
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
  nsExec::ExecToStack 'tasklist.exe /NH /FO CSV /FI "IMAGENAME eq ${PROCESS_NAME}"'
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
  nsExec::ExecToStack 'tasklist.exe /NH /FO CSV /FI "IMAGENAME eq ${PROCESS_NAME}"'
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
  !insertmacro SetInstallContext
  Call EnsureAppClosed
  SetOutPath "$INSTDIR"
  File /r "${INPUT_GLOB}"
{{resource_install_commands}}

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

  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "Comments" "${PRODUCT_DESCRIPTION}"
{{homepage_registry}}
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${MAIN_EXECUTABLE}"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "EstimatedSize" ${ESTIMATED_SIZE}
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  !insertmacro SetInstallContext
  Call un.EnsureAppClosed
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  DeleteRegKey SHCTX "${UNINSTALL_KEY}"
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
