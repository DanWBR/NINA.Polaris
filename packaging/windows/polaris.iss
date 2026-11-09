; Polaris installer for Windows (Inno Setup 6.3 or newer).
;
; Wraps the same self-contained publish output as polaris-win-<arch>.zip, so an
; installed Polaris updates itself exactly like the portable one: the in-app
; updater downloads the zip and polaris-update.ps1 swaps the folders.
;
; Layout, per user, no administrator rights needed:
;   %LOCALAPPDATA%\Programs\Polaris\            install root, uninstaller, helpers
;   %LOCALAPPDATA%\Programs\Polaris\app\        the publish output (NINA.Polaris.exe)
;   %LOCALAPPDATA%\Programs\Polaris\app.previous\   kept by the updater after a swap
;
; The updater renames app\ away and moves the new build in, which is why the
; uninstaller and every shortcut target live outside it: a swapped folder still
; has the same path, and the uninstaller is not inside the folder that moves.
; It also needs the parent folder writable by the user, which is why this is a
; per-user install and not one under Program Files.
;
; Build (CI passes all three):
;   ISCC.exe /DAppVersion=0.98.254 /DArch=x64 /DSourceDir=<publish dir> packaging\windows\polaris.iss
; Output: polaris-setup-win-<arch>.exe in /O (default: the current directory).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the dotnet publish output for win-{#Arch}>
#endif

#if Arch == "x64"
  #define ArchAllowed "x64compatible"
#elif Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #error Arch must be x64 or arm64
#endif

; A preview tag (0.99.220-preview1) is not a valid file version.
#if Pos("-", AppVersion) > 0
  #define NumVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumVersion AppVersion
#endif

#define AppName "Polaris"
#define ExeName "NINA.Polaris.exe"
#define PowerShell "{sys}\WindowsPowerShell\v1.0\powershell.exe"
; polaris-open.ps1 starts Polaris unless it is running and opens the page once it answers.
#define OpenArgs '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\polaris-open.ps1"" -InstallDir ""{app}""'

[Setup]
; Never change the AppId: it is how a new setup finds the existing install, and
; polaris-update.ps1 refreshes DisplayVersion under the same key.
AppId={{f4228c17-673e-4a65-8628-416979934996}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Polaris Astro Controller
AppPublisherURL=https://polaris-astro.app.br/
AppSupportURL=https://github.com/DanWBR/NINA.Polaris/issues
AppUpdatesURL=https://polaris-astro.app.br/install/
VersionInfoVersion={#NumVersion}
DefaultDirName={localappdata}\Programs\Polaris
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0.17763
OutputBaseFilename=polaris-setup-win-{#Arch}
SetupIconFile=..\..\src\NINA.Polaris\wwwroot\favicon.ico
UninstallDisplayIcon={app}\polaris.ico
UninstallDisplayName={#AppName}
LicenseFile=..\..\LICENSE.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.TaskFirewall=Allow phones, tablets and other computers on my network to reach Polaris (asks for administrator approval)
ptbr.TaskFirewall=Permitir que celulares, tablets e outros computadores da minha rede acessem o Polaris (pede aprovação de administrador)
es.TaskFirewall=Permitir que teléfonos, tabletas y otros ordenadores de mi red accedan a Polaris (pide aprobación de administrador)
fr.TaskFirewall=Autoriser les téléphones, tablettes et autres ordinateurs de mon réseau à accéder à Polaris (demande l'accord d'un administrateur)
de.TaskFirewall=Smartphones, Tablets und anderen Computern im Netzwerk den Zugriff auf Polaris erlauben (fragt nach Administratorfreigabe)
en.TaskStartup=Start Polaris when I sign in to Windows
ptbr.TaskStartup=Iniciar o Polaris quando eu entrar no Windows
es.TaskStartup=Iniciar Polaris al iniciar sesión en Windows
fr.TaskStartup=Démarrer Polaris à l'ouverture de ma session Windows
de.TaskStartup=Polaris bei der Windows-Anmeldung starten
en.OpenInBrowser=Open Polaris in the browser
ptbr.OpenInBrowser=Abrir o Polaris no navegador
es.OpenInBrowser=Abrir Polaris en el navegador
fr.OpenInBrowser=Ouvrir Polaris dans le navigateur
de.OpenInBrowser=Polaris im Browser öffnen
en.LaunchNow=Start Polaris and open it in the browser
ptbr.LaunchNow=Iniciar o Polaris e abrir no navegador
es.LaunchNow=Iniciar Polaris y abrirlo en el navegador
fr.LaunchNow=Démarrer Polaris et l'ouvrir dans le navigateur
de.LaunchNow=Polaris starten und im Browser öffnen

[Tasks]
Name: "firewall"; Description: "{cm:TaskFirewall}"
Name: "startup"; Description: "{cm:TaskStartup}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The operator's settings survive a reinstall over an existing copy; the
; in-app updater is what merges new keys into them.
Source: "{#SourceDir}\appsettings*.json"; DestDir: "{app}\app"; Flags: onlyifdoesntexist
Source: "{#SourceDir}\*"; Excludes: "appsettings*.json"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "polaris-stop.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "polaris-open.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\src\NINA.Polaris\wwwroot\favicon.ico"; DestDir: "{app}"; DestName: "polaris.ico"; Flags: ignoreversion

[InstallDelete]
; Leftovers of an interrupted self-update from an earlier install.
Type: filesandordirs; Name: "{app}\app.new-*"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\{#ExeName}"; WorkingDir: "{app}\app"; IconFilename: "{app}\polaris.ico"
Name: "{group}\{cm:OpenInBrowser}"; Filename: "{#PowerShell}"; Parameters: "{#OpenArgs}"; WorkingDir: "{app}"; IconFilename: "{app}\polaris.ico"; Flags: runminimized
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{#PowerShell}"; Parameters: "{#OpenArgs}"; WorkingDir: "{app}"; IconFilename: "{app}\polaris.ico"; Flags: runminimized; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\app\{#ExeName}"; WorkingDir: "{app}\app"; IconFilename: "{app}\polaris.ico"; Flags: runminimized; Tasks: startup

[Run]
Filename: "{#PowerShell}"; Parameters: "{#OpenArgs}"; WorkingDir: "{app}"; Description: "{cm:LaunchNow}"; Flags: postinstall nowait skipifsilent runhidden

[UninstallDelete]
; The updater's folders; app\ itself also holds files a newer version brought
; in that this setup never recorded.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\app.*"
Type: dirifempty; Name: "{app}"

[Code]
const
  FirewallRule = 'Polaris Astro Controller';

procedure StopPolaris(const Helper: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Helper +
    '" -InstallDir "' + ExpandConstant('{app}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if DirExists(ExpandConstant('{app}\app')) then begin
    ExtractTemporaryFile('polaris-stop.ps1');
    StopPolaris(ExpandConstant('{tmp}\polaris-stop.ps1'));
  end;
end;

{ Reading the firewall works without elevation; only changing it asks. }
function FirewallRuleExists(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall show rule name="' + FirewallRule + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure RunElevatedPowerShell(const Command: String);
var
  Code: Integer;
begin
  if not ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Firewall change not made (declined or failed): ' + SysErrorMessage(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') and not FirewallRuleExists() then
    RunElevatedPowerShell(
      'New-NetFirewallRule -DisplayName ''' + FirewallRule + ''' -Direction Inbound -Protocol TCP ' +
      '-LocalPort 5000,5080 -Action Allow -Profile Private,Domain | Out-Null; ' +
      'New-NetFirewallRule -DisplayName ''mDNS (Polaris)'' -Direction Inbound -Protocol UDP ' +
      '-LocalPort 5353 -Action Allow -Profile Private,Domain | Out-Null');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopPolaris(ExpandConstant('{app}\polaris-stop.ps1'));
  if (CurUninstallStep = usPostUninstall) and FirewallRuleExists() then
    RunElevatedPowerShell(
      'Remove-NetFirewallRule -DisplayName ''' + FirewallRule + ''' -ErrorAction SilentlyContinue; ' +
      'Remove-NetFirewallRule -DisplayName ''mDNS (Polaris)'' -ErrorAction SilentlyContinue');
end;
