; VISOR Inno Setup Script
; This creates the installer for VISOR - Virtual Intelligence Simulator Overlay & Radar

#define MyAppName "VISOR"
#define MyAppPublisher "CephasMedia LLC"
#define MyAppSupportEmail "mailto:info@cephasmedia.com"
#define MyAppExeName "VISOR.exe"

; Read version from compiled executable
#define MyAppVersion GetVersionNumbersString("bin\Release\net8.0-windows8.0\VISOR.exe")

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

; Windows version requirements (Windows 10 1809 or later for .NET 8)
MinVersion=10.0.17763

; Architecture (use x64 since .NET 8 typically targets x64)
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
Source: "bin\Release\net8.0-windows8.0\VISOR.exe"; DestDir: "{app}"; Flags: ignoreversion

; All DLL dependencies
Source: "bin\Release\net8.0-windows8.0\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

; Configuration files
Source: "bin\Release\net8.0-windows8.0\*.json"; DestDir: "{app}"; Flags: ignoreversion;

; Track section catalog (named corners for the Row 5 location readout)
Source: "bin\Release\net8.0-windows8.0\Data\TrackSections.json"; DestDir: "{app}\Data"; Flags: ignoreversion

; Runtime config
Source: "bin\Release\net8.0-windows8.0\*.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion

; Resources are embedded in the executable, not separate files
; Source: "bin\Release\net8.0-windows8.0\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists('bin\Release\net8.0-windows8.0\Resources')

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

// Check for .NET 8 Runtime
function IsDotNet8Installed: Boolean;
var
  Version: String;
begin
  Result := False;
  // Check registry for Desktop Runtime 8.0.x
  if RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\desktop', 'Version', Version) then
  begin
    if (Length(Version) > 0) and (Copy(Version, 1, 2) = '8.') then
    begin
      Result := True;
      Exit;
    end;
  end;
  
  // Fallback: Check if runtime files exist
  if FileExists(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.0\Microsoft.WindowsDesktop.App.dll')) or
     DirExists(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.10')) then
  begin
    Result := True;
  end;
end;

procedure InitializeWizard;
begin
  if not IsDotNet8Installed then
  begin
    DotNetRuntimeDownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading .NET 8 Desktop Runtime', nil);
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
  if (CurPageID = wpReady) and not IsDotNet8Installed then
  begin
    DotNetRuntimeDownloadPage.Clear;
    DotNetRuntimeDownloadPage.Add('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', 'windowsdesktop-runtime-8.0-win-x64.exe', '');
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
  if not IsDotNet8Installed then
  begin
    RuntimeInstaller := ExpandConstant('{tmp}\windowsdesktop-runtime-8.0.10-win-x64.exe');
    if FileExists(RuntimeInstaller) then
    begin
      if MsgBox('.NET 8 Desktop Runtime will now be installed. This may take a few minutes.' + #13#10 + #13#10 + 'Continue?', mbConfirmation, MB_YESNO) = IDYES then
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
        Result := '.NET 8 Desktop Runtime is required to run VISOR';
      end;
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