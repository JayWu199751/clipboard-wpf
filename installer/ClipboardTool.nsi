; ClipboardTool NSIS 安装包（F38；ADR-0001：自包含、新产品身份、x64 perMachine）
; 产物：installer/ClipboardTool-Setup.exe（约 70–100MB，携带 .NET 10 桌面运行时）
; 构建：installer/build-installer.ps1（先 dotnet publish 自包含，再 makensis）
;
; 行为契约（工单 12）：
;   安装 = Program Files\ClipboardTool + 开始菜单/桌面快捷方式 + 完成后提权启动（安装器已是管理员，子进程静默继承）
;   升级 = 同产品 GUID，先静默卸载旧版本再安装；计划任务 exe 路径由应用下次提权启动时自收敛（F36 判定表）
;   卸载 = 移除计划任务 ClipboardToolElevated 与全部快捷方式；用户存档 %APPDATA%\ClipboardTool 默认保留（卸载时明确告知）

Unicode true
ManifestSupportedOS all
RequestExecutionLevel admin

!include "MUI2.nsh"
!include "x64.nsh"

!define PRODUCT_NAME "ClipboardTool"
!define PRODUCT_VERSION "1.0.0.0"
!define PRODUCT_PUBLISHER "ClipboardTool Project"
!define PRODUCT_GUID "{7E4A9C31-8F2D-4B6A-9C05-3A1D8E52F7B4}"
!define TASK_NAME "ClipboardToolElevated"
; 注册表键名用 GUID（新产品身份）：旧 Tauri 版在 Uninstall\ClipboardTool（64 位视图）留有键，
; 同名会覆盖旧键违背 ADR-0001「互不覆盖」；GUID 键两代互不干扰
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_GUID}"
!define APP_REGKEY "Software\${PRODUCT_GUID}"

Name "${PRODUCT_NAME}"
OutFile "ClipboardTool-Setup.exe"
InstallDir "$PROGRAMFILES64\${PRODUCT_NAME}"
InstallDirRegKey HKLM "${APP_REGKEY}" "InstallDir"

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\ClipboardTool.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${PRODUCT_NAME}（提权）"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; perMachine：x64 校验 + 64 位注册表视图 + 全机上下文（快捷方式写到所有用户的开始菜单/桌面）
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "本安装包仅支持 64 位 Windows（ADR-0001：Win10 22H2+ / Win11）。"
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Section "安装"
  ; 升级=覆盖安装（NSIS 惯例）：不做先卸后装——那样会连计划任务一起删掉；
  ; 文件直接覆盖，快捷方式与注册表就地重建，任务 exe 路径变化由应用下次
  ; 提权启动按 F36 判定表重注册收敛（升级更新任务 exe 路径）。
  nsExec::Exec 'taskkill /IM "${PRODUCT_NAME}.exe" /F'
  Sleep 500

  SetOutPath "$INSTDIR"
  File /r "publish\*.*"

  ; 快捷方式（所有用户）
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_NAME}.exe"
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_NAME}.exe"

  ; 卸载器与注册表（新产品身份：独立 productName/GUID，与旧 Tauri 版互不覆盖）
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${APP_REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_NAME}.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

  ; 计划任务不在安装期创建：应用首次提权启动时按 F36 判定表自建（意图为准）
SectionEnd

Section "Uninstall"
  ; 卸载界面明确说明：存档默认保留（/SD=静默卸载时自动应答）
  MessageBox MB_ICONINFORMATION \
    "即将卸载 ${PRODUCT_NAME}。$\n$\n将移除：程序文件、快捷方式、计划任务 ${TASK_NAME}。$\n将保留：你的剪贴板存档（%APPDATA%\ClipboardTool），可随时手动删除或供重装后继续使用。" \
    /SD IDOK
  nsExec::Exec 'taskkill /IM "${PRODUCT_NAME}.exe" /F'
  Sleep 500

  ; 计划任务（失败不阻断——可能本就不存在）
  nsExec::ExecToLog 'schtasks /Delete /TN "${TASK_NAME}" /F'

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "${APP_REGKEY}"
  DetailPrint "用户存档 %APPDATA%\ClipboardTool 已保留。"
SectionEnd
