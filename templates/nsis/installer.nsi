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
!define CAPABILITIES_KEY "Software\${PRODUCT_ID}\Capabilities"
!define INSTALL_MARKER ".dotnet-bundler-${PRODUCT_ID}"
!define SIGNED_UNINSTALLER "{{signed_uninstaller}}"
!define SHORTCUT_DESKTOP_PATH "{{shortcut_desktop_path}}"
!define SHORTCUT_START_MENU_DIRECTORY "{{shortcut_start_menu_directory}}"
!define SHORTCUT_START_MENU_PATH "{{shortcut_start_menu_path}}"
!define SHORTCUT_TARGET "$INSTDIR\${MAIN_EXECUTABLE}"
!define SHORTCUT_OWNED_TARGETS "{{shortcut_owned_targets}}"
!define SHORTCUT_ARGUMENTS "{{shortcut_arguments}}"
!define SHORTCUT_WORKING_DIRECTORY "{{shortcut_working_directory}}"
!define SHORTCUT_ICON "{{shortcut_icon}}"
!define SHORTCUT_APP_USER_MODEL_ID "{{shortcut_app_user_model_id}}"
{{uninstaller_import_define}}
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
Var PassiveMode
Var UpdateMode
Var NoShortcutMode
Var RestartApplication
Var LaunchArguments
Var ExitCode

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
; 命令行调用方可以依赖这些稳定退出码判断安装结果。
!define EXIT_SUCCESS 0
!define EXIT_CANCELLED 1
!define EXIT_FAILURE 2
!define EXIT_INVALID_ARGUMENTS 3
!define EXIT_VERSION_BLOCKED 4
!define EXIT_APP_CLOSE_FAILED 5
!define EXIT_REBOOT_REQUIRED 3010

Name "${PRODUCT_NAME}"
BrandingText "${PRODUCT_PUBLISHER}"
OutFile "${OUTPUT_FILE}"
; 签名模式的第一次编译完成后，将生成的卸载器交给宿主签名。
{{uninstaller_finalize_command}}
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
!define MUI_CUSTOMFUNCTION_ABORT RecordUserAbort
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApplication
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "Software\${PRODUCT_ID}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "Installer Language"

!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfPassive
!insertmacro MUI_PAGE_WELCOME
{{license_page}}
!if "${INSTALL_MODE}" == "both"
  !define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfPassive
  !insertmacro MULTIUSER_PAGE_INSTALLMODE
!endif
; 在所选 Shell 上下文中检测现有安装，并决定如何替换它。
; 不存在旧版本时自动跳过此页面。
Page custom ExistingInstallPage
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfPassive
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
Page custom ShortcutOptionsPage ShortcutOptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfPassive
!insertmacro MUI_PAGE_FINISH
!define MUI_PAGE_CUSTOMFUNCTION_PRE un.SkipIfPassive
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

!macro CheckShortcutResult
  ; 原生 Shell 操作失败时终止安装，避免把“已选择但未创建”误报为成功。
  ${If} $0 < 0
    StrCpy $ExitCode ${EXIT_FAILURE}
    SetErrorLevel $ExitCode
    Abort
  ${EndIf}
!macroend

Function ParseCommandLine
  ; /P 显示安装进度但跳过所有需要输入的页面。
  ${GetOptions} $CMDLINE "/P" $0
  ${IfNot} ${Errors}
    StrCpy $PassiveMode 1
  ${EndIf}

  ; /UPDATE 是自动更新器入口；除非同时使用 /S，否则默认采用被动模式。
  ${GetOptions} $CMDLINE "/UPDATE" $0
  ${IfNot} ${Errors}
    StrCpy $UpdateMode 1
    ; 更新器保留现有快捷方式状态，不补建用户已经删除的快捷方式。
    StrCpy $CreateDesktopShortcut 0
    StrCpy $CreateStartMenuShortcut 0
    ${IfNot} ${Silent}
      StrCpy $PassiveMode 1
    ${EndIf}
  ${EndIf}

  ; /NS 禁止本次安装创建桌面和开始菜单快捷方式。
  ${GetOptions} $CMDLINE "/NS" $0
  ${IfNot} ${Errors}
    StrCpy $NoShortcutMode 1
    StrCpy $CreateDesktopShortcut 0
    StrCpy $CreateStartMenuShortcut 0
  ${EndIf}

  ; /R 只允许自动化安装使用，避免与完成页的交互式启动选项冲突。
  ${GetOptions} $CMDLINE "/R" $0
  ${IfNot} ${Errors}
    ${IfNot} ${Silent}
    ${AndIf} $PassiveMode != 1
      StrCpy $ExitCode ${EXIT_INVALID_ARGUMENTS}
      SetErrorLevel $ExitCode
      Abort "$(InvalidRestartMode)"
    ${EndIf}
    StrCpy $RestartApplication 1
  ${EndIf}

  ; /ARGS= 读取到下一个安装器选项，便于把 NSIS 要求位于末尾的 /D= 放在最后；
  ; 兼容的 /ARGS 写法则把后续全部文本视为应用参数。
  ${GetOptions} $CMDLINE "/ARGS=" $LaunchArguments
  ${If} ${Errors}
    ${GetOptions} $CMDLINE "/ARGS" $LaunchArguments
  ${EndIf}
  ${IfNot} ${Errors}
    ${If} $RestartApplication != 1
      StrCpy $ExitCode ${EXIT_INVALID_ARGUMENTS}
      SetErrorLevel $ExitCode
      Abort "$(ArgumentsRequireRestart)"
    ${EndIf}
  ${Else}
    StrCpy $LaunchArguments ""
  ${EndIf}
FunctionEnd

Function SkipIfPassive
  ${If} $PassiveMode == 1
    Abort
  ${EndIf}
FunctionEnd

Function un.SkipIfPassive
  ${If} $PassiveMode == 1
    Abort
  ${EndIf}
FunctionEnd

Function LaunchApplication
  DotNetBundlerNsis::RunAsUser "$INSTDIR\${MAIN_EXECUTABLE}" "$LaunchArguments"
  Pop $0
  ${If} $0 != 0
    StrCpy $ExitCode ${EXIT_FAILURE}
    ${IfNot} ${Silent}
    ${AndIf} $PassiveMode != 1
      MessageBox MB_ICONSTOP|MB_OK "$(ApplicationLaunchFailed)"
    ${EndIf}
  ${EndIf}
FunctionEnd

Function .onInit
  StrCpy $ExitCode ${EXIT_SUCCESS}
  StrCpy $PassiveMode 0
  StrCpy $UpdateMode 0
  StrCpy $NoShortcutMode 0
  StrCpy $RestartApplication 0
  StrCpy $LaunchArguments ""
  StrCpy $CreateDesktopShortcut {{shortcut_desktop_default}}
  StrCpy $CreateStartMenuShortcut {{shortcut_start_menu_default}}
  StrCpy $ExistingInstallAction 0
  Call ParseCommandLine
  ; 静默与被动模式都不能显示语言选择器。
  ${IfNot} ${Silent}
  ${AndIf} $PassiveMode != 1
{{display_language_selector}}
  ${EndIf}
  !insertmacro SetInstallContext
  !if "${INSTALL_MODE}" == "both"
    !insertmacro MULTIUSER_INIT
  !else
    Call SetDefaultInstallDirectory
  !endif

  ; 静默和被动安装不会显示现有安装处理页面，因此需要在初始化阶段确定处理方式。
  ; 交互式安装则在对应页面中确定处理方式。
  Call DetectExistingInstall
  ${If} ${Silent}
    Call ApplyAutomatedExistingInstallPolicy
    Call ValidateAutomatedInstallDirectory
  ${ElseIf} $PassiveMode == 1
    Call ApplyAutomatedExistingInstallPolicy
    Call ValidateAutomatedInstallDirectory
  ${EndIf}
FunctionEnd

Function RecordUserAbort
  ${If} $ExitCode == ${EXIT_SUCCESS}
    StrCpy $ExitCode ${EXIT_CANCELLED}
  ${EndIf}
  SetErrorLevel $ExitCode
FunctionEnd

Function .onInstFailed
  ${If} $ExitCode == ${EXIT_SUCCESS}
    StrCpy $ExitCode ${EXIT_FAILURE}
  ${EndIf}
  SetErrorLevel $ExitCode
FunctionEnd

Function .onInstSuccess
  ; 需要重新启动时不立即运行应用，让自动化调用方先处理 3010。
  IfRebootFlag reboot_required no_reboot_required
  reboot_required:
    SetErrorLevel ${EXIT_REBOOT_REQUIRED}
    Return
  no_reboot_required:
    ${If} $RestartApplication == 1
      Call LaunchApplication
    ${EndIf}
    SetErrorLevel $ExitCode
FunctionEnd

Function un.onInit
  StrCpy $ExitCode ${EXIT_SUCCESS}
  StrCpy $PassiveMode 0
  StrCpy $DeleteAppData 0
  ${GetOptions} $CMDLINE "/P" $0
  ${IfNot} ${Errors}
    StrCpy $PassiveMode 1
  ${EndIf}
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

Function ApplyAutomatedExistingInstallPolicy
  ${If} $InstalledUninstaller == ""
    Return
  ${EndIf}

  ; MSI 与 NSIS 的载荷记录不兼容，因此迁移时不能原位覆盖。
  ${If} $ExistingInstallType == ${EXISTING_TYPE_MSI}
    ${If} $VersionComparison == ${VERSION_OLDER}
      !if "${ALLOW_DOWNGRADES}" != "true"
        StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
        SetErrorLevel $ExitCode
        Abort "$(SilentDowngradeBlocked)"
      !endif
    ${ElseIf} $VersionComparison == ${VERSION_UNKNOWN}
      StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
      SetErrorLevel $ExitCode
      Abort "$(SilentUnknownVersionBlocked)"
    ${EndIf}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
    Return
  ${EndIf}

  ; 自动更新采用原位覆盖，以保留用户选择和运行时数据；它仍然遵守降级策略。
  ${If} $UpdateMode == 1
    ${If} $VersionComparison == ${VERSION_OLDER}
      !if "${ALLOW_DOWNGRADES}" != "true"
        StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
        SetErrorLevel $ExitCode
        Abort "$(SilentDowngradeBlocked)"
      !endif
    ${ElseIf} $VersionComparison == ${VERSION_UNKNOWN}
      StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
      SetErrorLevel $ExitCode
      Abort "$(SilentUnknownVersionBlocked)"
    ${EndIf}
    ${If} $InstalledDirectory != ""
      StrCpy $INSTDIR $InstalledDirectory
    ${EndIf}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_INSTALL_OVER}
    Return
  ${EndIf}

  ; 同版本自动安装会就地修复文件；普通自动升级会先删除旧版打包载荷，
  ; 同时保留应用数据。
  ${If} $VersionComparison == ${VERSION_SAME}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_INSTALL_OVER}
  ${ElseIf} $VersionComparison == ${VERSION_NEWER}
    StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
  ${ElseIf} $VersionComparison == ${VERSION_OLDER}
    !if "${ALLOW_DOWNGRADES}" == "true"
      StrCpy $ExistingInstallAction ${EXISTING_ACTION_UNINSTALL_FIRST}
    !else
      ; 被禁止的自动降级必须直接返回失败，不能等待用户界面交互。
      StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
      SetErrorLevel $ExitCode
      Abort "$(SilentDowngradeBlocked)"
    !endif
  ${Else}
    ; 缺少交互式确认时，无法安全处理无法识别的版本。
    StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
    SetErrorLevel $ExitCode
    Abort "$(SilentUnknownVersionBlocked)"
  ${EndIf}
FunctionEnd

Function ExistingInstallPage
  ${If} $PassiveMode == 1
    Abort
  ${EndIf}
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
        StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
        SetErrorLevel $ExitCode
        Quit
      !endif
    ${EndIf}
    MessageBox MB_ICONQUESTION|MB_OKCANCEL "$(LegacyMsiDetected)" IDOK existing_uninstall_first
    StrCpy $ExitCode ${EXIT_CANCELLED}
    SetErrorLevel $ExitCode
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
      StrCpy $ExitCode ${EXIT_VERSION_BLOCKED}
      SetErrorLevel $ExitCode
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
    StrCpy $ExitCode ${EXIT_CANCELLED}
    SetErrorLevel $ExitCode
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
    ${OrIf} $PassiveMode == 1
      StrCpy $ExitCode ${EXIT_FAILURE}
      SetErrorLevel $ExitCode
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
    ${ElseIf} $PassiveMode == 1
      ExecWait '"$SYSDIR\msiexec.exe" /x "$LegacyMsiProductCode" /passive /norestart' $0
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
    ${OrIf} $PassiveMode == 1
      StrCpy $ExitCode ${EXIT_FAILURE}
      SetErrorLevel $ExitCode
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

Function ValidateAutomatedInstallDirectory
  ; 自动模式不能通过对话框确认风险：只允许空目录或带本产品标记的目录。
  IfFileExists "$INSTDIR\${INSTALL_MARKER}" automated_directory_valid
  FindFirst $0 $1 "$INSTDIR\*"
  automated_directory_scan:
    StrCmp $1 "" automated_directory_empty
    StrCmp $1 "." automated_directory_next
    StrCmp $1 ".." automated_directory_next
    FindClose $0
    StrCpy $ExitCode ${EXIT_INVALID_ARGUMENTS}
    SetErrorLevel $ExitCode
    Abort "$(AutomatedNonEmptyDirectoryBlocked)"
  automated_directory_next:
    FindNext $0 $1
    Goto automated_directory_scan
  automated_directory_empty:
    FindClose $0
  automated_directory_valid:
FunctionEnd

Function ShortcutOptionsPage
  ${If} $PassiveMode == 1
  ${OrIf} $NoShortcutMode == 1
  ${OrIf} $UpdateMode == 1
    Abort
  ${EndIf}
  ; 已安装产品的选择会成为重新安装时的默认值；不存在记录时沿用包配置。
  ClearErrors
  ReadRegDWORD $0 SHCTX "${UNINSTALL_KEY}" "ShortcutDesktop"
  ${IfNot} ${Errors}
    StrCpy $CreateDesktopShortcut $0
  ${EndIf}
  ClearErrors
  ReadRegDWORD $0 SHCTX "${UNINSTALL_KEY}" "ShortcutStartMenu"
  ${IfNot} ${Errors}
    StrCpy $CreateStartMenuShortcut $0
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "$(ShortcutPageTitle)" "$(ShortcutPageSubtitle)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateCheckbox} 0 20u 100% 12u "$(DesktopShortcutLabel)"
  Pop $DesktopShortcutCheckbox
  ${If} $CreateDesktopShortcut == 1
    ${NSD_Check} $DesktopShortcutCheckbox
  ${EndIf}
  ${NSD_CreateCheckbox} 0 48u 100% 12u "$(StartMenuShortcutLabel)"
  Pop $StartMenuShortcutCheckbox
  ${If} $CreateStartMenuShortcut == 1
    ${NSD_Check} $StartMenuShortcutCheckbox
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function ConfigureShortcuts
  ; /UPDATE /NS 明确要求本次更新完全不触碰快捷方式。
  ${If} $UpdateMode == 1
  ${AndIf} $NoShortcutMode == 1
    Return
  ${EndIf}

  ; 先迁移配置声明的旧产品名快捷方式；插件只会移动目标仍属于本安装的项。
{{shortcut_migration_commands}}

  ${If} $UpdateMode == 1
    ; 更新只刷新仍存在且仍归本产品所有的快捷方式，不重建用户手动删除的项。
    DotNetBundlerNsis::UpdateShortcutIfOwned "${SHORTCUT_DESKTOP_PATH}" "${SHORTCUT_OWNED_TARGETS}" "${SHORTCUT_TARGET}" "${SHORTCUT_ARGUMENTS}" "${SHORTCUT_WORKING_DIRECTORY}" "${SHORTCUT_ICON}" "${SHORTCUT_APP_USER_MODEL_ID}"
    Pop $0
    !insertmacro CheckShortcutResult
    DotNetBundlerNsis::UpdateShortcutIfOwned "${SHORTCUT_START_MENU_PATH}" "${SHORTCUT_OWNED_TARGETS}" "${SHORTCUT_TARGET}" "${SHORTCUT_ARGUMENTS}" "${SHORTCUT_WORKING_DIRECTORY}" "${SHORTCUT_ICON}" "${SHORTCUT_APP_USER_MODEL_ID}"
    Pop $0
    !insertmacro CheckShortcutResult
    Return
  ${EndIf}

  ${If} $CreateDesktopShortcut == 1
    DotNetBundlerNsis::CreateShortcut "${SHORTCUT_DESKTOP_PATH}" "${SHORTCUT_OWNED_TARGETS}" "${SHORTCUT_TARGET}" "${SHORTCUT_ARGUMENTS}" "${SHORTCUT_WORKING_DIRECTORY}" "${SHORTCUT_ICON}" "${SHORTCUT_APP_USER_MODEL_ID}"
    Pop $0
    !insertmacro CheckShortcutResult
  ${Else}
    DotNetBundlerNsis::DeleteShortcutIfOwned "${SHORTCUT_DESKTOP_PATH}" "${SHORTCUT_OWNED_TARGETS}"
    Pop $0
    !insertmacro CheckShortcutResult
  ${EndIf}

  ${If} $CreateStartMenuShortcut == 1
    CreateDirectory "${SHORTCUT_START_MENU_DIRECTORY}"
    DotNetBundlerNsis::CreateShortcut "${SHORTCUT_START_MENU_PATH}" "${SHORTCUT_OWNED_TARGETS}" "${SHORTCUT_TARGET}" "${SHORTCUT_ARGUMENTS}" "${SHORTCUT_WORKING_DIRECTORY}" "${SHORTCUT_ICON}" "${SHORTCUT_APP_USER_MODEL_ID}"
    Pop $0
    !insertmacro CheckShortcutResult
  ${Else}
    DotNetBundlerNsis::DeleteShortcutIfOwned "${SHORTCUT_START_MENU_PATH}" "${SHORTCUT_OWNED_TARGETS}"
    Pop $0
    !insertmacro CheckShortcutResult
    RMDir "${SHORTCUT_START_MENU_DIRECTORY}"
  ${EndIf}

  ; 选择与实际创建分开持久化，后续交互式重装可以恢复用户偏好。
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "ShortcutDesktop" $CreateDesktopShortcut
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "ShortcutStartMenu" $CreateStartMenuShortcut
FunctionEnd

Function un.RemoveOwnedShortcuts
  ; 删除前由原生插件解析 .lnk 目标；同名快捷方式被其他程序接管时保持不变。
  DotNetBundlerNsis::DeleteShortcutIfOwned "${SHORTCUT_DESKTOP_PATH}" "${SHORTCUT_OWNED_TARGETS}"
  Pop $0
  DotNetBundlerNsis::DeleteShortcutIfOwned "${SHORTCUT_START_MENU_PATH}" "${SHORTCUT_OWNED_TARGETS}"
  Pop $0
{{shortcut_legacy_cleanup_commands}}
  RMDir "${SHORTCUT_START_MENU_DIRECTORY}"
FunctionEnd

Function ShortcutOptionsLeave
  ${NSD_GetState} $DesktopShortcutCheckbox $CreateDesktopShortcut
  ${NSD_GetState} $StartMenuShortcutCheckbox $CreateStartMenuShortcut
FunctionEnd

Function un.AppDataOptionsPage
  ${If} $PassiveMode == 1
    Abort
  ${EndIf}
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
  IfSilent close_app check_passive_close
  check_passive_close:
  ${If} $PassiveMode == 1
    Goto close_app
  ${EndIf}
  MessageBox MB_ICONEXCLAMATION|MB_OKCANCEL "$(AppRunningPrompt)" IDOK close_app IDCANCEL cancel_close
  close_app:
    nsExec::ExecToStack 'taskkill.exe /F /T /IM "${PROCESS_NAME}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" app_closed
    StrCpy $ExitCode ${EXIT_APP_CLOSE_FAILED}
    SetErrorLevel $ExitCode
    ${IfNot} ${Silent}
    ${AndIf} $PassiveMode != 1
      MessageBox MB_ICONSTOP "$(AppCloseFailed)"
    ${EndIf}
    Abort
  cancel_close:
    StrCpy $ExitCode ${EXIT_CANCELLED}
    SetErrorLevel $ExitCode
    Abort
  app_closed:
FunctionEnd

Function un.EnsureAppClosed
  nsExec::ExecToStack 'tasklist.exe /NH /FO CSV /FI "IMAGENAME eq ${PROCESS_NAME}"'
  Pop $0
  Pop $1
  ${UnStrStr} $2 "$1" "${PROCESS_NAME}"
  StrCmp $2 "" un_app_closed
  IfSilent un_close_app un_check_passive_close
  un_check_passive_close:
  ${If} $PassiveMode == 1
    Goto un_close_app
  ${EndIf}
  MessageBox MB_ICONEXCLAMATION|MB_OKCANCEL "$(AppRunningPrompt)" IDOK un_close_app IDCANCEL un_cancel_close
  un_close_app:
    nsExec::ExecToStack 'taskkill.exe /F /T /IM "${PROCESS_NAME}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" un_app_closed
    StrCpy $ExitCode ${EXIT_APP_CLOSE_FAILED}
    SetErrorLevel $ExitCode
    ${IfNot} ${Silent}
    ${AndIf} $PassiveMode != 1
      MessageBox MB_ICONSTOP "$(AppCloseFailed)"
    ${EndIf}
    Abort
  un_cancel_close:
    StrCpy $ExitCode ${EXIT_CANCELLED}
    SetErrorLevel $ExitCode
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

  ; 第二次编译直接封装已经签名的卸载器，避免安装后得到未签名文件。
  !ifdef BUNDLER_IMPORT_SIGNED_UNINSTALLER
    File "/oname=Uninstall.exe" "${SIGNED_UNINSTALLER}"
  !else
    WriteUninstaller "$INSTDIR\Uninstall.exe"
  !endif

  Call ConfigureShortcuts

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
{{association_install_commands}}
  !ifmacrodef NSIS_HOOK_POSTINSTALL
    !insertmacro NSIS_HOOK_POSTINSTALL
  !endif
  ; 被动模式只保留进度窗口，并在成功后自动关闭。
  ${If} $PassiveMode == 1
    SetAutoClose true
  ${EndIf}
SectionEnd

; 导入签名卸载器时不再生成新的卸载段；卸载逻辑已包含在签名文件中。
!ifndef BUNDLER_IMPORT_SIGNED_UNINSTALLER
Section "Uninstall"
  !insertmacro SetInstallContext
  !ifmacrodef NSIS_HOOK_PREUNINSTALL
    !insertmacro NSIS_HOOK_PREUNINSTALL
  !endif
  Call un.EnsureAppClosed
  Call un.RemoveOwnedShortcuts
{{association_uninstall_commands}}
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
  ; 被动卸载只显示进度，并在完成后自动关闭。
  ${If} $PassiveMode == 1
    SetAutoClose true
  ${EndIf}
SectionEnd
!endif
