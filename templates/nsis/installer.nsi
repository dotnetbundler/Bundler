Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

; 在解析任何插件命令之前，先注册随包提供的 Unicode 插件目录。
; NSIS 插件 ABI 为 32 位，因此该插件编译为 win-x86。
!addplugindir "{{plugin_directory}}"

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
!define ALLOW_DOWNGRADES "{{allow_downgrades}}"
!define LEGACY_MSI_PRODUCT_CODES "{{legacy_msi_product_codes}}"
!define LEGACY_MSI_UPGRADE_CODES "{{legacy_msi_upgrade_codes}}"
!define INPUT_GLOB "{{input_glob}}"
!define OUTPUT_FILE "{{output_file}}"
!define ESTIMATED_SIZE "{{estimated_size}}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"
!define INSTALL_MARKER ".dotnet-bundler-${PRODUCT_ID}"
{{installer_icon_directives}}
{{installer_hooks_include}}

Var CreateDesktopShortcut
Var CreateStartMenuShortcut
Var DesktopShortcutCheckbox
Var StartMenuShortcutCheckbox
Var DeleteAppData
Var DeleteAppDataCheckbox
Var InstalledVersion
Var InstalledUninstaller
Var InstalledDirectory
Var VersionComparison
Var ExistingInstallAction
Var ExistingInstallType
Var LegacyMsiProductCode

; DotNetBundlerNsis::SemverCompare 返回的比较结果。
; 将这些名称放在状态变量附近，便于理解各个版本策略分支。
!define VERSION_OLDER -1
!define VERSION_SAME 0
!define VERSION_NEWER 1
!define VERSION_UNKNOWN 2
!define EXISTING_ACTION_INSTALL_OVER 1
!define EXISTING_ACTION_UNINSTALL_FIRST 2
!define EXISTING_TYPE_NSIS 1
!define EXISTING_TYPE_MSI 2

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
; 在所选 Shell 上下文中检测现有安装，并决定如何替换它。
; 不存在旧版本时自动跳过此页面。
Page custom ExistingInstallPage
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
  StrCpy $ExistingInstallAction 0
{{display_language_selector}}
  !insertmacro SetInstallContext
  !if "${INSTALL_MODE}" == "both"
    !insertmacro MULTIUSER_INIT
  !else
    Call SetDefaultInstallDirectory
  !endif

  ; 静默安装不会显示现有安装处理页面，因此需要在初始化阶段确定处理方式。
  ; 交互式安装则在对应页面中确定处理方式。
  Call DetectExistingInstall
  ${If} ${Silent}
    Call ApplySilentExistingInstallPolicy
  ${EndIf}
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

Function DetectExistingInstall
  ; SHCTX 会根据所选安装范围映射到 HKCU 或 HKLM。
  ; 必须存在 UninstallString，避免将残缺的注册表项误判为已安装。
  ReadRegStr $InstalledUninstaller SHCTX "${UNINSTALL_KEY}" "UninstallString"
  ReadRegStr $InstalledDirectory SHCTX "${UNINSTALL_KEY}" "InstallLocation"
  ReadRegStr $InstalledVersion SHCTX "${UNINSTALL_KEY}" "DisplayVersion"
  StrCpy $VersionComparison ${VERSION_UNKNOWN}
  StrCpy $ExistingInstallType 0
  ${If} $InstalledUninstaller == ""
    StrCpy $InstalledVersion ""
    StrCpy $InstalledDirectory ""
    Call DetectLegacyMsiInstallation
  ${Else}
    StrCpy $ExistingInstallType ${EXISTING_TYPE_NSIS}
    Call CompareInstalledVersion
  ${EndIf}
FunctionEnd

Function CompareInstalledVersion
  ; 打包时无法知道用户已安装的版本，因此在安装器运行时进行比较。
  ; 随包提供的插件实现 SemVer 2.0，并支持预发布版本。
  DotNetBundlerNsis::SemverCompare "${PRODUCT_VERSION}" "$InstalledVersion"
  Pop $VersionComparison
FunctionEnd

Function DetectLegacyMsiInstallation
  ; 只按显式配置的 ProductCode 或 UpgradeCode 查找，避免因名称相同而误卸载其他软件。
  DotNetBundlerNsis::FindMsiProduct "${LEGACY_MSI_PRODUCT_CODES}" "${LEGACY_MSI_UPGRADE_CODES}"
  Pop $LegacyMsiProductCode
  ${If} $LegacyMsiProductCode == ""
    Return
  ${EndIf}

  ; 多个相关 MSI 并存时使用最高版本，避免因枚举顺序而绕过降级限制。
  DotNetBundlerNsis::GetNewestMsiVersion "${LEGACY_MSI_PRODUCT_CODES}" "${LEGACY_MSI_UPGRADE_CODES}"
  Pop $InstalledVersion
  StrCpy $InstalledUninstaller "$SYSDIR\msiexec.exe"
  StrCpy $InstalledDirectory ""
  StrCpy $ExistingInstallType ${EXISTING_TYPE_MSI}
  Call CompareInstalledVersion
FunctionEnd

Function ApplySilentExistingInstallPolicy
  ${If} $InstalledUninstaller == ""
    Return
  ${EndIf}

  ; MSI 与 NSIS 的载荷记录不兼容，因此迁移时不能原位覆盖。
  ${If} $ExistingInstallType == ${EXISTING_TYPE_MSI}
    ${If} $VersionComparison == ${VERSION_OLDER}
      !if "${ALLOW_DOWNGRADES}" != "true"
        SetErrorLevel 2
        Abort "$(SilentDowngradeBlocked)"
      !endif
    ${ElseIf} $VersionComparison == ${VERSION_UNKNOWN}
      SetErrorLevel 2
      Abort "$(SilentUnknownVersionBlocked)"
    ${EndIf}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
    Return
  ${EndIf}

  ; 同版本静默安装会就地修复文件；静默升级会先删除旧版打包载荷，
  ; 同时保留应用数据。
  ${If} $VersionComparison == ${VERSION_SAME}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_INSTALL_OVER}
  ${ElseIf} $VersionComparison == ${VERSION_NEWER}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
  ${ElseIf} $VersionComparison == ${VERSION_OLDER}
    !if "${ALLOW_DOWNGRADES}" == "true"
      StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
    !else
      ; 被禁止的静默降级必须直接返回失败，不能等待用户界面交互。
      SetErrorLevel 2
      Abort "$(SilentDowngradeBlocked)"
    !endif
  ${Else}
    ; 缺少交互式确认时，无法安全处理无法识别的版本。
    SetErrorLevel 2
    Abort "$(SilentUnknownVersionBlocked)"
  ${EndIf}
FunctionEnd

Function ExistingInstallPage
  ; 安装模式页面可能改变 SHCTX，因此在显示交互式版本策略选项之前，
  ; 必须重新检测现有安装。
  Call DetectExistingInstall
  ${If} $InstalledUninstaller == ""
    Abort
  ${EndIf}

  ; 旧 MSI 必须先卸载；仍沿用统一的降级禁止策略。
  ${If} $ExistingInstallType == ${EXISTING_TYPE_MSI}
    ${If} $VersionComparison == ${VERSION_OLDER}
      !if "${ALLOW_DOWNGRADES}" != "true"
        MessageBox MB_ICONSTOP|MB_OK "$(DowngradeBlocked)"
        SetErrorLevel 2
        Quit
      !endif
    ${EndIf}
    MessageBox MB_ICONQUESTION|MB_OKCANCEL "$(LegacyMsiDetected)" IDOK existing_uninstall_first
    Quit
  ${EndIf}

  ${If} $VersionComparison == ${VERSION_SAME}
    MessageBox MB_ICONQUESTION|MB_YESNOCANCEL "$(SameVersionDetected)" IDYES existing_install_over IDNO existing_uninstall_first
    Goto existing_cancel
  ${ElseIf} $VersionComparison == ${VERSION_NEWER}
    MessageBox MB_ICONQUESTION|MB_YESNOCANCEL "$(UpgradeDetected)" IDYES existing_uninstall_first IDNO existing_install_over
    Goto existing_cancel
  ${ElseIf} $VersionComparison == ${VERSION_OLDER}
    !if "${ALLOW_DOWNGRADES}" == "true"
      MessageBox MB_ICONEXCLAMATION|MB_YESNOCANCEL "$(DowngradeDetected)" IDYES existing_uninstall_first IDNO existing_install_over
      Goto existing_cancel
    !else
      MessageBox MB_ICONSTOP|MB_OK "$(DowngradeBlocked)"
      SetErrorLevel 2
      Quit
    !endif
  ${Else}
    MessageBox MB_ICONEXCLAMATION|MB_YESNOCANCEL "$(UnknownVersionDetected)" IDYES existing_uninstall_first IDNO existing_install_over
    Goto existing_cancel
  ${EndIf}

  existing_install_over:
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_INSTALL_OVER}
    Abort
  existing_uninstall_first:
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
    Abort
  existing_cancel:
    Quit
FunctionEnd

Function UninstallExistingInstallation
  ${If} $ExistingInstallAction != ${EXISTING_ACTION_UNINSTALL_FIRST}
    Return
  ${EndIf}

  ${If} $ExistingInstallType == ${EXISTING_TYPE_MSI}
    Call UninstallLegacyMsiInstallations
    Return
  ${EndIf}

  ; `_?=` 使旧版卸载器在原安装目录中运行，而不是使用临时副本。
  ; 默认的静默卸载会保留应用数据。
  DetailPrint "$(RemovingExistingVersion)"
  ClearErrors
  ExecWait '$InstalledUninstaller /S _?=$InstalledDirectory' $0
  ${If} ${Errors}
  ${OrIf} $0 != 0
  ${OrIf} ${FileExists} "$InstalledDirectory\${MAIN_EXECUTABLE}"
    ${If} ${Silent}
      SetErrorLevel 3
      Quit
    ${Else}
      MessageBox MB_ICONSTOP|MB_OK "$(ExistingUninstallFailed)"
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd

Function UninstallLegacyMsiInstallations
  ; 每次重新查询第一个匹配项，确保同一 UpgradeCode 下的多个遗留版本都被清理。
  legacy_msi_loop:
    DotNetBundlerNsis::FindMsiProduct "${LEGACY_MSI_PRODUCT_CODES}" "${LEGACY_MSI_UPGRADE_CODES}"
    Pop $LegacyMsiProductCode
    ${If} $LegacyMsiProductCode == ""
      Return
    ${EndIf}

    DetailPrint "$(RemovingLegacyMsiVersion)"
    ClearErrors
    ${If} ${Silent}
      ExecWait '"$SYSDIR\msiexec.exe" /x "$LegacyMsiProductCode" /qn /norestart' $0
    ${Else}
      ExecWait '"$SYSDIR\msiexec.exe" /x "$LegacyMsiProductCode" /passive /norestart' $0
    ${EndIf}

    ; 1605 表示产品已不存在；1641 和 3010 表示操作成功但需要重新启动。
    ${If} $0 == 0
      Goto legacy_msi_loop
    ${ElseIf} $0 == 1605
      Goto legacy_msi_loop
    ${ElseIf} $0 == 1641
      SetRebootFlag true
      Goto legacy_msi_loop
    ${ElseIf} $0 == 3010
      SetRebootFlag true
      Goto legacy_msi_loop
    ${EndIf}

    ${If} ${Silent}
      SetErrorLevel 3
      Quit
    ${Else}
      MessageBox MB_ICONSTOP|MB_OK "$(ExistingUninstallFailed)"
      Abort
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
  ; 在执行新版安装前 Hook 之前完成旧版本替换，确保 Hook 看到的是最终的
  ; 安装目录状态，而不是旧版本遗留的文件。
  Call UninstallExistingInstallation
  !ifmacrodef NSIS_HOOK_PREINSTALL
    !insertmacro NSIS_HOOK_PREINSTALL
  !endif
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
  !ifmacrodef NSIS_HOOK_POSTINSTALL
    !insertmacro NSIS_HOOK_POSTINSTALL
  !endif
SectionEnd

Section "Uninstall"
  !insertmacro SetInstallContext
  !ifmacrodef NSIS_HOOK_PREUNINSTALL
    !insertmacro NSIS_HOOK_PREUNINSTALL
  !endif
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
  !ifmacrodef NSIS_HOOK_POSTUNINSTALL
    !insertmacro NSIS_HOOK_POSTUNINSTALL
  !endif
SectionEnd
