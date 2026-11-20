#define MyAppName "Wuwatappo Native"
#ifndef MyVersion
  #define MyVersion "0.3.5"
#endif
#ifndef MyDisplayVersion
  #define MyDisplayVersion MyVersion
#endif
#ifndef MyFileVersion
  #define MyFileVersion "0.3.5.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\publish\app-" + MyVersion
#endif

[Setup]
AppId={{A7C3F6D2-5B1E-4E8A-9C2D-8F4B6A1D3E57}
AppName={#MyAppName}
AppVersion={#MyDisplayVersion}
AppPublisher=Wuwatappo Native
; 使用 {autopf} 而不是固定的 {localappdata}：
;   - “为所有用户安装”时解析到 {commonpf}\Wuwatappo Native，卸载信息和快捷方式不会指向某个用户的 LocalAppData；
;   - “仅当前用户安装”时解析到 {localappdata}\Programs\Wuwatappo Native，保留原来的默认位置。
; 用户可以在目录页自行更改 {app}；卸载程序、开始菜单和桌面快捷方式都基于 {app}/{group}/{autodesktop}，
; 会自动跟随安装模式和自定义路径。
DefaultDirName={autopf}\Wuwatappo Native
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
; 必须显式指定分组名；否则 Inno Setup 会回退到字面量“(Default)”文件夹。
DefaultGroupName={#MyAppName}
; 默认管理员模式；安装模式对话框可选择“为所有用户安装（推荐）”或“仅当前用户”标准权限安装。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; 旧版本是以 lowest 安装的；不能沿用旧安装模式，否则 admin 默认值不会生效。
UsePreviousPrivileges=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=WuwatappoNative-{#MyDisplayVersion}-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\WuwatappoNative.exe
Compression=lzma2
SolidCompression=yes
; 暖色/奶油色向导：左侧大图与右上角小图都由仓库内素材生成，不引入外部依赖。
; 同时提供多个 DPI 尺寸，Inno Setup 会按当前缩放比例自动选择最接近的一张。
WizardStyle=modern
; Inno Setup 默认跳过欢迎页；这里显式开启，让左侧大幅插画和欢迎文案出现在第一步。
DisableWelcomePage=no
WizardImageFile=..\assets\installer\wizard-large-*.png
WizardSmallImageFile=..\assets\installer\wizard-small-*.png
WizardImageStretch=yes
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter=WuwatappoNative.exe
RestartApplications=no
VersionInfoVersion={#MyFileVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyFileVersion}
VersionInfoDescription={#MyAppName} 安装包

[Files]
Source: "{#MySourceDir}\WuwatappoNative.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; {group}/{autodesktop} 是模式自适应常量：所有用户安装写入公共开始菜单/桌面，仅当前用户安装写入当前用户目录。
; 快捷方式始终指向实际选择的 {app}，因此自定义安装目录不会断开。
Name: "{group}\{#MyAppName}"; Filename: "{app}\WuwatappoNative.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\WuwatappoNative.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\WuwatappoNative.exe"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""WuwatappoNative"" /F"; Flags: runhidden; RunOnceId: "DeleteAutoStartTask"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v WuwatappoNative /f"; Flags: runhidden; RunOnceId: "DeleteRunValue"

[Messages]
; 覆盖 Inno Setup 默认英文文案，保持 Tappo 的暖色、轻松语气。
SetupAppTitle=Wuwatappo Native 安装程序
SetupWindowTitle=Wuwatappo Native 安装程序
UninstallAppTitle=Wuwatappo Native 卸载程序
UninstallAppFullTitle=卸载 %1
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
WelcomeLabel1=欢迎使用WuwaTappoNative
WelcomeLabel2=即将安装 [name] {#MyDisplayVersion}。%n%nNavive重构Tappo，更轻，更快，更好用。%n%n安装前建议先退出正在运行的 Wuwatappo Native；安装程序也会帮你确认。
ClickNext=点击“下一步”继续，或点击“取消”退出安装。
WizardSelectDir=选择安装位置
SelectDirDesc=把 Tappo 安顿在哪里？
SelectDirLabel3=安装程序会把 [name] 放进下面的文件夹。%n你可以点击“浏览”选择其他位置。
SelectDirBrowseLabel=确认后点击“下一步”；想换一个文件夹，请点击“浏览”。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用空间。
WizardReady=准备安装
ReadyLabel1=准备就绪，[name] 期待和你见面。
ReadyLabel2a=点击“安装”开始；想再检查安装位置，可以点击“上一步”。
ReadyLabel2b=点击“安装”开始。
ReadyMemoDir=安装位置：
ReadyMemoGroup=开始菜单文件夹：
WizardPreparing=正在准备
PreparingDesc=正在准备安装 [name]……
WizardInstalling=正在安装
InstallingLabel=正在安抚团子，请稍候……
FinishedHeadingLabel=欢迎！小可爱们已经准备好了
FinishedLabel=安装完成！%n%n[name] 已经准备就绪，可以从开始菜单或桌面快捷方式启动。%n%n点击“完成”就可以去和它打招呼了。
FinishedLabelNoIcons=安装完成！%n%n[name] 已经准备就绪。%n%n点击“完成”退出安装程序。
ClickFinish=点击“完成”退出安装程序，然后去和你的桌宠朋友们打个招呼吧。
StatusClosingApplications=正在关闭正在运行的程序……
StatusCreateDirs=正在创建文件夹……
StatusExtractFiles=正在解压文件……
StatusCreateIcons=正在创建快捷方式……
StatusSavingUninstall=正在保存卸载信息……
StatusRunProgram=正在完成安装……
StatusRollback=正在回滚更改……
StatusUninstalling=正在卸载 %1……
ConfirmUninstall=确定要卸载 %1 吗？%n%n用户设置、日志和缓存不会被删除。
SetupAppRunningError=检测到 %1 正在运行。%n%n请先从托盘退出程序，再点击“确定”重试，或点击“取消”退出。
UninstallAppRunningError=检测到 %1 正在运行。%n%n请先从托盘退出程序，再点击“确定”重试，或点击“取消”退出。
ExitSetupTitle=退出安装
ExitSetupMessage=安装还没有完成。现在退出，程序将不会安装。%n%n确定要退出安装程序吗？

[Code]
procedure InitializeWizard;
begin
  { 让欢迎页/完成页的标题跟随 Tappo 的暖棕色；TColor 为 $BBGGRR。 }
  WizardForm.WelcomeLabel1.Font.Color := $002D456E;
  WizardForm.FinishedHeadingLabel.Font.Color := $002D456E;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CheckForMutexes('Local\WuwatappoNative.SingleInstance.v1') then
  begin
    if MsgBox('检测到 Wuwatappo Native 正在运行。安装前需要关闭它，是否现在关闭并继续？', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
    if (not Exec('taskkill.exe', '/IM WuwatappoNative.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      if IsAdmin then
      begin
        MsgBox('无法自动关闭 Wuwatappo Native，可能它正以管理员权限运行。请先从托盘退出程序，再重新运行安装包。', mbError, MB_OK);
        Result := False;
      end
      else if MsgBox('当前为标准权限，无法自动结束管理员模式运行的桌宠。请先手动退出桌宠后继续安装；是否仍继续？', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;
