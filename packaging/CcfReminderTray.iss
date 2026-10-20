; 会议截稿提醒（CcfReminderTray）Inno Setup 脚本
; 固定当前用户安装（PrivilegesRequired=lowest，不提供切换为其他安装模式的选项）。
; 卸载行为：
;  - 检测到应用仍在运行时，AppMutex 会提示用户先从托盘正常退出并重试（或中止卸载）；
;    它只是提示并阻止继续，不会自动关闭任何进程，更不会按进程名强杀其他目录的副本。
;  - 清理开机启动项前先核对 HKCU Run\CcfReminderTray 的值：仅当它指向本次卸载的
;    {app}\CcfReminderTray.exe 时才删除；指向其他位置（如便携版）时保留。
;  - 用户数据（设置/提醒状态/缓存/手动修正及其损坏备份）由用户选择保留或删除，不静默删除。
; 构建：先 dotnet publish 到 artifacts\publish，再运行 ISCC.exe packaging\CcfReminderTray.iss

#define MyAppName "会议截稿提醒"
#define MyAppVersion "1.0.4"
#define MyAppExeName "CcfReminderTray.exe"

[Setup]
AppId={{B7E2A4C1-5D3F-4E8A-9C6D-2F1B8A7E5D4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=个人项目（数据来源 ccfddl，未逐项核验）
DefaultDirName={localappdata}\Programs\CcfReminderTray
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=CcfReminderTray-Setup-{#MyAppVersion}
UninstallDisplayName={#MyAppName} (CcfReminderTray)
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 应用运行时持有此单实例 Mutex：安装/卸载检测到它会提示用户先从托盘正常退出，
; 用户退出后点“重试”继续，或点“中止”取消。这是提示并阻止继续，不是自动关闭。
; CloseApplications=no：不让安装程序经 Restart Manager 代为关闭应用。
AppMutex=Local\CcfReminderTray.SingleInstance
CloseApplications=no

[Languages]
; 简体中文消息文件来自 Inno Setup 官方仓库 jrsoftware/issrc（is-6_7_3 标签，随脚本一起留存，
; 来源与维护者标注见 THIRD-PARTY-NOTICES.txt 第 3 节）
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "..\artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 卸载清理开机启动项：仅当 HKCU Run\CcfReminderTray 指向本次卸载的
// 安装目录下 CcfReminderTray.exe（带引号或不带引号）时才删除；
// 无值、或已指向其他位置（如其他目录的便携版）时一律保留。
procedure RemoveRunValueIfOurs;
var
  Existing, ExpectedQuoted, ExpectedPlain: String;
begin
  if RegQueryStringValue(HKCU,
       'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
       'CcfReminderTray', Existing) then
  begin
    ExpectedPlain := ExpandConstant('{app}\{#MyAppExeName}');
    ExpectedQuoted := '"' + ExpectedPlain + '"';
    if (CompareText(Existing, ExpectedQuoted) = 0) or
       (CompareText(Existing, ExpectedPlain) = 0) then
      RegDeleteValue(HKCU,
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'CcfReminderTray');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveRunValueIfOurs;
    // 用户数据：明确询问保留或删除，绝不静默删除。
    // DelTree 删除的是整个 DataDir，确认框文字必须覆盖该目录全部内容
    // （设置、提醒状态、缓存、overrides.json 手动修正及 *.corrupt-*.bak 备份），
    // 并说明删除不可恢复、安装版与便携版共享该目录。
    DataDir := ExpandConstant('{localappdata}\CcfReminderTray');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除你的个人数据？该目录包含：' + #13#10 +
                '· 设置（通知开关、阈值档位）与提醒状态' + #13#10 +
                '· 数据缓存（离线回退用的最近快照）' + #13#10 +
                '· 你手动修正的截止时间（overrides.json）' + #13#10 +
                '· 损坏修正文件的备份（overrides.json.corrupt-*.bak）' + #13#10#13#10 +
                DataDir + #13#10#13#10 +
                '选择“是”将删除整个目录，手动修正与备份删除后无法恢复；' + #13#10 +
                '安装版与便携版共享此目录，删除对两者同时生效。' + #13#10 +
                '选择“否”保留全部内容，以后重装可继续使用。',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
