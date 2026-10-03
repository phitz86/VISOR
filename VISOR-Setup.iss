; VISOR Inno Setup Script
; This creates the installer for VISOR - Virtual Intelligence Simulator Overlay & Radar

#define MyAppName "VISOR"
#define MyAppPublisher "CephasMedia LLC"
#define MyAppSupportEmail "mailto:info@cephasmedia.com"
#define MyAppExeName "VISOR.exe"

; Build output folder; must match <TargetFramework> in VISOR.csproj
#define BuildDir "bin\Release\net10.0-windows"

; .NET Desktop Runtime major version VISOR runs on (framework-dependent build)
#define DotNetMajor "10"

; Read version from compiled executable
#define MyAppVersion GetVersionNumbersString(BuildDir + "\VISOR.exe")

[Setup]
; Basic app info
AppId={{5c7df855-82e4-42f8-b31b-7e6d3171f735}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL={#MyAppSupportEmail}

; Default installation directory
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Allow user to choose install location
DisableProgramGroupPage=no

; Welcome page
DisableWelcomePage=no

; License agreement
LicenseFile=LICENSE.txt

; Output settings
OutputDir=installer
OutputBaseFilename=VISOR-Setup-{#MyAppVersion}
SetupIconFile=VISOR Logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

; Compression
Compression=lzma2/max
SolidCompression=yes

; Windows version requirements (Windows 10 1809 or later)
MinVersion=10.0.17763

; Architecture (x64 only; the installer fetches the x64 .NET Desktop Runtime)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Privileges (install to Program Files requires admin)
PrivilegesRequired=admin

; Detect a running VISOR during install/uninstall so an upgrade can close it
; before its files are replaced (otherwise the locked VISOR.exe can't be removed).
; The name must match SingleInstanceMutexName in App.xaml.cs; it's in the Global\
; namespace so this elevated (admin) installer can see the mutex created by the
; app running as the normal user.
AppMutex=Global\VISOR_SingleInstance_Mutex

; Use the Windows Restart Manager to close VISOR automatically if it's running
; (falls back to the AppMutex "please close it" prompt if it can't). Don't
; auto-relaunch afterward — the optional post-install "Launch VISOR" task owns that.
CloseApplications=yes
RestartApplications=no

; Visual style
WizardStyle=modern
WizardImageFile=VISOR Install Banner logo.bmp
WizardSmallImageFile=VISOR Install Logo.bmp

[Messages]
WelcomeLabel1=Welcome to [name] Setup
WelcomeLabel2=This will install [name/ver] on your computer.%n%nVISOR is an overlay for iRacing that provides real-time race information including relative positioning, fuel calculations, and proximity radar.%n%nIt is recommended that you close iRacing before continuing.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Main executable
Source: "{#BuildDir}\VISOR.exe"; DestDir: "{app}"; Flags: ignoreversion

; All DLL dependencies
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

; Configuration files
Source: "{#BuildDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion;

; Track section catalog (named corners for the Row 5 location readout)
Source: "{#BuildDir}\Data\TrackSections.json"; DestDir: "{app}\Data"; Flags: ignoreversion

; Runtime config
Source: "{#BuildDir}\*.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion

; Resources are embedded in the executable, not separate files
; Source: "{#BuildDir}\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists('{#BuildDir}\Resources')

; Icon file
Source: "VISOR Logo.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu shortcut
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\VISOR Logo.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

; Desktop shortcut (optional, based on user choice)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\VISOR Logo.ico"; Tasks: desktopicon

[Run]
; Option to launch VISOR after installation
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DotNetRuntimeDownloadPage: TDownloadWizardPage;
  DonateLabel: TNewStaticText;

procedure DonateLabelClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://venmo.com/u/Pete-Hitzeman', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

const
  // Microsoft's "latest patch of this major version" link, so a fresh install
  // always gets the current, security-patched runtime rather than a pinned one.
  DotNetRuntimeUrl = 'https://aka.ms/dotnet/{#DotNetMajor}.0/windowsdesktop-runtime-win-x64.exe';
  // The download is saved under this name and run from it; one constant so the
  // two can't drift apart.
  DotNetRuntimeFile = 'windowsdesktop-runtime-{#DotNetMajor}-win-x64.exe';
  DotNetRuntimeName = '.NET {#DotNetMajor} Desktop Runtime';
  DotNetRuntimeManualUrl = 'https://dotnet.microsoft.com/download/dotnet/{#DotNetMajor}.0';

// True if a released (non-preview) build of the required major version is in a
// list of installed versions such as '10.0.3' or '9.0.11'.
function HasRequiredVersion(const Versions: TArrayOfString): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Versions) - 1 do
  begin
    // Previews ('10.0.0-rc.2...') don't satisfy a 10.0.0 app, so skip them.
    if (Pos('{#DotNetMajor}.', Versions[I]) = 1) and (Pos('-', Versions[I]) = 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Check for the .NET Desktop Runtime VISOR needs. Any patch of the right major
// version will do: the app rolls forward to the newest patch installed.
function IsDotNetInstalled: Boolean;
var
  Versions: TArrayOfString;
  FindRec: TFindRec;
  Count: Integer;
begin
  Result := False;

  // Primary: the folder the .NET host itself loads the framework from.
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\{#DotNetMajor}.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Count := GetArrayLength(Versions);
          SetArrayLength(Versions, Count + 1);
          Versions[Count] := FindRec.Name;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if HasRequiredVersion(Versions) then
  begin
    Result := True;
    Exit;
  end;

  // Fallback: the runtime installer records each installed version as a value
  // name under this key (in the 32-bit registry view, even for x64 runtimes).
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) and
     HasRequiredVersion(Versions) then
    Result := True
  else if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) and
     HasRequiredVersion(Versions) then
    Result := True;
end;

// Refuse to run the downloaded runtime installer unless Windows reports a valid
// Authenticode signature from Microsoft. This checks the file's integrity and
// origin without pinning a hash (and therefore a version), so new installs keep
// getting the latest patch. Fails closed: if the check can't run, nothing is run.
function IsSignedByMicrosoft(const FileName: String): Boolean;
var
  QuotedPath, Script: String;
  ResultCode: Integer;
begin
  Result := False;
  // PowerShell single-quoted string: escape any ' in the path by doubling it.
  QuotedPath := FileName;
  StringChange(QuotedPath, '''', '''''');
  Script :=
    '$s = Get-AuthenticodeSignature -LiteralPath ''' + QuotedPath + '''; ' +
    'if ($s.Status -ne ''Valid'') { exit 1 }; ' +
    'if ($s.SignerCertificate.Subject -notlike ''CN=Microsoft Corporation,*'') { exit 2 }; ' +
    'exit 0';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
          '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Runtime installer signature check exit code: ' + IntToStr(ResultCode));
    Result := (ResultCode = 0);
  end
  else
    Log('Runtime installer signature check could not run: ' + SysErrorMessage(ResultCode));
end;

procedure InitializeWizard;
begin
  if not IsDotNetInstalled then
  begin
    DotNetRuntimeDownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading ' + DotNetRuntimeName, nil);
  end;

  // Create the Donation Link on the Finished Page
  DonateLabel := TNewStaticText.Create(WizardForm);
  DonateLabel.Parent := WizardForm.FinishedPage;
  DonateLabel.Caption := 'Support VISOR development! Click here to donate via Venmo.';
  DonateLabel.Cursor := crHand;
  DonateLabel.Font.Color := clBlue;
  DonateLabel.Font.Style := [fsUnderline];
  
  DonateLabel.Left := ScaleX(176); 
  DonateLabel.Top := ScaleY(220);  
  
  DonateLabel.OnClick := @DonateLabelClick;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not IsDotNetInstalled then
  begin
    DotNetRuntimeDownloadPage.Clear;
    DotNetRuntimeDownloadPage.Add(DotNetRuntimeUrl, DotNetRuntimeFile, '');
    DotNetRuntimeDownloadPage.Show;
    try
      try
        DotNetRuntimeDownloadPage.Download;
        Result := True;
      except
        if DotNetRuntimeDownloadPage.AbortedByUser then
          Log('Download aborted by user.')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DotNetRuntimeDownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  RuntimeInstaller: String;
begin
  Result := '';
  if not IsDotNetInstalled then
  begin
    RuntimeInstaller := ExpandConstant('{tmp}\' + DotNetRuntimeFile);
    if not FileExists(RuntimeInstaller) then
    begin
      Result := 'The ' + DotNetRuntimeName + ' download could not be found. ' +
                'Please install it from ' + DotNetRuntimeManualUrl + ' and run Setup again.';
      Exit;
    end;

    if not IsSignedByMicrosoft(RuntimeInstaller) then
    begin
      Result := 'The downloaded ' + DotNetRuntimeName + ' installer did not pass its Microsoft ' +
                'signature check, so it was not run. Please install the runtime from ' +
                DotNetRuntimeManualUrl + ' and run Setup again.';
      Exit;
    end;

    if MsgBox(DotNetRuntimeName + ' will now be installed. This may take a few minutes.' + #13#10 + #13#10 + 'Continue?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      if Exec(RuntimeInstaller, '/quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode = 0 then
        begin
          Log('.NET Runtime installed successfully');
        end
        else
        begin
          Result := '.NET Runtime installation failed with code: ' + IntToStr(ResultCode);
        end;
      end
      else
      begin
        Result := 'Failed to execute .NET Runtime installer';
      end;
    end
    else
    begin
      Result := DotNetRuntimeName + ' is required to run VISOR';
    end;
  end;
end;

// Handle upgrades: wipe the previous version's files before installing the new
// set, so anything that shipped in an older release but not this one (orphaned
// DLLs, renamed/removed dependencies, stale runtimes\ or satellite-resource
// folders) doesn't linger in {app} and risk being loaded at runtime.
//
// ssInstall fires before Inno copies the new payload, so this is synchronous and
// race-free. It is safe to clear {app}: all user data (settings, logs,
// diagnostics) lives under %LOCALAPPDATA%\VISOR, never here. We only wipe {app}
// when our own exe is present there — that confirms it's a prior VISOR install
// rather than some unrelated folder the user may have chosen. Inno then recreates
// {app}, installs the current files, and refreshes its own uninstaller; the
// stable AppId keeps a single Programs-and-Features entry across upgrades.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    begin
      DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;