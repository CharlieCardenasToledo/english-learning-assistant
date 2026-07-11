; ============================================================================
; English Learning Assistant — Inno Setup script
;
; Installs the app per-user (no admin needed, mirrors LM Studio's own model)
; and optionally provisions the full AI stack:
;   1. LM Studio headless daemon + lms CLI  (official install.ps1, always latest)
;   2. llama-3.2-3b-instruct model          (lms get, ~2 GB)
;
; Build:  ISCC.exe installer\EnglishLearningAssistant.iss
; Output: installer\Output\EnglishLearningAssistant-Setup.exe
; ============================================================================

#define MyAppName "English Learning Assistant"
#define MyAppVersion "1.2.2"
#define MyAppPublisher "Charlie Cardenas Toledo"
#define MyAppURL "https://github.com/CharlieCardenasToledo/english-learning-assistant"
#define MyAppExeName "EnglishLearningAssistant.exe"
#define ModelUrl "https://huggingface.co/lmstudio-community/Llama-3.2-3B-Instruct-GGUF"

[Setup]
AppId={{8F2A1C3D-5E6B-4A7F-9D8C-1B2E3F4A5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=EnglishLearningAssistant-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.InstallLmStudio=Instalar LM Studio (motor de IA local) — recomendado
spanish.DownloadModel=Descargar modelo de IA llama-3.2-3b (~2 GB) — recomendado
spanish.AiTasksGroup=Componentes de IA (necesarios para el asistente):
spanish.InstallingLmStudio=Instalando LM Studio (esto puede tardar unos minutos)...
spanish.DownloadingModel=Descargando modelo de IA (~2 GB, puede tardar varios minutos)...
spanish.LmStudioDetected=LM Studio ya está instalado en este equipo.
english.InstallLmStudio=Install LM Studio (local AI engine) — recommended
english.DownloadModel=Download llama-3.2-3b AI model (~2 GB) — recommended
english.AiTasksGroup=AI components (required by the assistant):
english.InstallingLmStudio=Installing LM Studio (this may take a few minutes)...
english.DownloadingModel=Downloading AI model (~2 GB, this may take several minutes)...
english.LmStudioDetected=LM Studio is already installed on this computer.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "installlm"; Description: "{cm:InstallLmStudio}"; GroupDescription: "{cm:AiTasksGroup}"; Check: not IsLmStudioInstalled
Name: "downloadmodel"; Description: "{cm:DownloadModel}"; GroupDescription: "{cm:AiTasksGroup}"; Check: not IsModelInstalled

[Files]
Source: "..\publish_release\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.es.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function GetLmsExePath(): String;
begin
  Result := ExpandConstant('{%USERPROFILE}\.lmstudio\bin\lms.exe');
end;

function IsLmStudioInstalled(): Boolean;
begin
  // lms CLI present (headless daemon or GUI app both ship it after bootstrap)
  Result := FileExists(GetLmsExePath())
    or DirExists(ExpandConstant('{localappdata}\Programs\LM Studio'))
    or DirExists(ExpandConstant('{localappdata}\LM-Studio'));
end;

function IsModelInstalled(): Boolean;
begin
  // Default lms download location for the bundled model
  Result := DirExists(ExpandConstant('{%USERPROFILE}\.lmstudio\models\lmstudio-community\Llama-3.2-3B-Instruct-GGUF'))
    or DirExists(ExpandConstant('{%USERPROFILE}\.cache\lm-studio\models\lmstudio-community\Llama-3.2-3B-Instruct-GGUF'));
end;

function RunHidden(const Cmd, Args: String; const StatusMsg: String): Boolean;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := StatusMsg;
  WizardForm.StatusLabel.Refresh();
  Result := Exec(Cmd, Args, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PsExe: String;
begin
  if CurStep <> ssPostInstall then Exit;

  PsExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');

  // 1. LM Studio headless daemon + lms CLI via the official installer script.
  //    Always fetches the latest version; installs per-user, no admin needed.
  if WizardIsTaskSelected('installlm') and not IsLmStudioInstalled() then
  begin
    if not RunHidden(PsExe,
        '-NoProfile -ExecutionPolicy Bypass -Command "irm https://lmstudio.ai/install.ps1 | iex"',
        CustomMessage('InstallingLmStudio')) then
      MsgBox('LM Studio installation failed. You can install it manually from https://lmstudio.ai',
        mbError, MB_OK);
  end;

  // 2. Model download through lms CLI (shows its own console progress).
  if WizardIsTaskSelected('downloadmodel') and FileExists(GetLmsExePath()) then
  begin
    if not RunHidden(GetLmsExePath(),
        'get {#ModelUrl} --yes',
        CustomMessage('DownloadingModel')) then
      MsgBox('Model download failed. You can download it later from LM Studio or with:'#13#10 +
        'lms get {#ModelUrl}', mbError, MB_OK);
  end;
end;
