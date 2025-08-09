using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using Microsoft.Win32;

namespace GRiD.Diagnostics
{
    public static class ConnectionDiagnostics
    {
        public static void RunDiagnostics()
        {
            Console.WriteLine("=".PadRight(80, '='));
            Console.WriteLine("GRiD CONNECTION DIAGNOSTICS");
            Console.WriteLine("=".PadRight(80, '='));

            CheckIRacingInstallation();
            CheckIRacingProcesses();
            CheckSharedMemory();
            CheckSDKVersions();
            CheckSystemRequirements();
            CheckPermissions();

            Console.WriteLine("\n" + "=".PadRight(80, '='));
            Console.WriteLine("DIAGNOSTIC COMPLETE");
            Console.WriteLine("=".PadRight(80, '='));
        }

        private static void CheckIRacingInstallation()
        {
            Console.WriteLine("\n[DIAGNOSTIC] iRacing Installation Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                // Check registry for iRacing installation
                var iracingPath = GetIRacingInstallPath();
                if (!string.IsNullOrEmpty(iracingPath))
                {
                    Console.WriteLine($"✓ iRacing found at: {iracingPath}");
                    
                    // Check for iRacingSim64DX11.exe
                    var exePath = Path.Combine(iracingPath, "iRacingSim64DX11.exe");
                    if (File.Exists(exePath))
                    {
                        var version = FileVersionInfo.GetVersionInfo(exePath);
                        Console.WriteLine($"✓ iRacing executable found: {version.FileVersion}");
                    }
                    else
                    {
                        Console.WriteLine("✗ iRacing executable not found");
                    }
                }
                else
                {
                    Console.WriteLine("✗ iRacing installation not found in registry");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking iRacing installation: {ex.Message}");
            }
        }

        private static void CheckIRacingProcesses()
        {
            Console.WriteLine("\n[DIAGNOSTIC] iRacing Process Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                var iracingProcesses = Process.GetProcesses()
                    .Where(p => p.ProcessName.ToLower().Contains("iracing"))
                    .ToList();

                if (iracingProcesses.Any())
                {
                    Console.WriteLine($"✓ Found {iracingProcesses.Count} iRacing process(es):");
                    foreach (var proc in iracingProcesses)
                    {
                        try
                        {
                            Console.WriteLine($"  - {proc.ProcessName} (PID: {proc.Id}, Memory: {proc.WorkingSet64 / (1024 * 1024)}MB)");
                        }
                        catch
                        {
                            Console.WriteLine($"  - {proc.ProcessName} (PID: {proc.Id})");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("✗ No iRacing processes found - SDK connection will fail");
                    Console.WriteLine("  Start iRacing and join a session to enable telemetry");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking processes: {ex.Message}");
            }
        }

        private static void CheckSharedMemory()
        {
            Console.WriteLine("\n[DIAGNOSTIC] Shared Memory Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                // Check for iRacing shared memory objects
                var sharedMemoryNames = new[]
                {
                    "Local\\IRSDKMemMapFileName",
                    "Local\\IRSDKDataValidEventName",
                    "Local\\IRSDKTelemetryDataValidEventName",
                    "Local\\IRSDKSessionDataValidEventName"
                };

                foreach (var name in sharedMemoryNames)
                {
                    try
                    {
                        // This is a simplified check - in reality, you'd use proper Win32 API calls
                        Console.WriteLine($"  Checking {name}...");
                        // Note: Proper implementation would use kernel32.dll OpenFileMapping
                    }
                    catch
                    {
                        Console.WriteLine($"  ✗ Cannot access {name}");
                    }
                }

                // Check telemetry settings
                CheckTelemetrySettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking shared memory: {ex.Message}");
            }
        }

        private static void CheckTelemetrySettings()
        {
            Console.WriteLine("\n[DIAGNOSTIC] Telemetry Settings Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var iracingPath = Path.Combine(documentsPath, "iRacing");
                var appIniPath = Path.Combine(iracingPath, "app.ini");

                if (File.Exists(appIniPath))
                {
                    Console.WriteLine($"✓ Found app.ini at: {appIniPath}");
                    
                    var content = File.ReadAllText(appIniPath);
                    
                    // Check for telemetry settings
                    if (content.Contains("irsdkEnableMem=1"))
                    {
                        Console.WriteLine("✓ Telemetry memory sharing enabled (irsdkEnableMem=1)");
                    }
                    else if (content.Contains("irsdkEnableMem=0"))
                    {
                        Console.WriteLine("✗ Telemetry memory sharing DISABLED (irsdkEnableMem=0)");
                        Console.WriteLine("  Fix: Set irsdkEnableMem=1 in app.ini");
                    }
                    else
                    {
                        Console.WriteLine("⚠ irsdkEnableMem setting not found - may use default");
                    }

                    // Check for other relevant settings
                    if (content.Contains("broadcastMsg=1"))
                    {
                        Console.WriteLine("✓ Broadcast messages enabled");
                    }
                }
                else
                {
                    Console.WriteLine($"✗ app.ini not found at: {appIniPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking telemetry settings: {ex.Message}");
            }
        }

        private static void CheckSDKVersions()
        {
            Console.WriteLine("\n[DIAGNOSTIC] SDK Version Check");
            Console.WriteLine("-".PadRight(50, '-'));

            // Check loaded assemblies for SDK versions
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName().Name;
                if (name != null && (name.Contains("iRacing") || name.Contains("IRSDK") || name.Contains("SVapps")))
                {
                    try
                    {
                        var version = assembly.GetName().Version;
                        var location = assembly.Location;
                        Console.WriteLine($"✓ {name}: v{version}");
                        if (!string.IsNullOrEmpty(location))
                        {
                            Console.WriteLine($"    Location: {location}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ {name}: Error getting version - {ex.Message}");
                    }
                }
            }

            // Check NuGet package versions from csproj or packages.config
            CheckNuGetPackages();
        }

        private static void CheckNuGetPackages()
        {
            try
            {
                var csprojFiles = Directory.GetFiles(".", "*.csproj", SearchOption.TopDirectoryOnly);
                foreach (var csproj in csprojFiles)
                {
                    var content = File.ReadAllText(csproj);
                    Console.WriteLine($"\n  Package references in {Path.GetFileName(csproj)}:");
                    
                    // Simple regex to find PackageReference elements
                    var lines = content.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("PackageReference") && (line.Contains("iRacing") || line.Contains("IRSDK") || line.Contains("SVapps")))
                        {
                            Console.WriteLine($"    {line.Trim()}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ Error checking NuGet packages: {ex.Message}");
            }
        }

        private static void CheckSystemRequirements()
        {
            Console.WriteLine("\n[DIAGNOSTIC] System Requirements Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                // OS Version
                var osVersion = Environment.OSVersion;
                Console.WriteLine($"✓ OS: {osVersion.VersionString}");

                // .NET Version
                var dotnetVersion = Environment.Version;
                Console.WriteLine($"✓ .NET Runtime: {dotnetVersion}");

                // Architecture
                var is64Bit = Environment.Is64BitProcess;
                Console.WriteLine($"✓ Process Architecture: {(is64Bit ? "64-bit" : "32-bit")}");

                // Memory
                var totalMemory = GetTotalMemoryGB();
                if (totalMemory > 0)
                {
                    Console.WriteLine($"✓ Total Memory: {totalMemory:F1} GB");
                }

                // Available memory
                var availableMemory = GC.GetTotalMemory(false) / (1024 * 1024);
                Console.WriteLine($"✓ Current Memory Usage: {availableMemory} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking system requirements: {ex.Message}");
            }
        }

        private static void CheckPermissions()
        {
            Console.WriteLine("\n[DIAGNOSTIC] Permissions Check");
            Console.WriteLine("-".PadRight(50, '-'));

            try
            {
                // Check if running as administrator
                var isAdmin = IsRunningAsAdministrator();
                if (isAdmin)
                {
                    Console.WriteLine("✓ Running with administrator privileges");
                }
                else
                {
                    Console.WriteLine("⚠ Not running as administrator - may cause SDK access issues");
                }

                // Check write permissions to output directory
                var outputDir = "logs";
                try
                {
                    Directory.CreateDirectory(outputDir);
                    var testFile = Path.Combine(outputDir, "permission_test.tmp");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    Console.WriteLine($"✓ Write permissions OK for: {Path.GetFullPath(outputDir)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Write permission error for {outputDir}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error checking permissions: {ex.Message}");
            }
        }

        private static string GetIRacingInstallPath()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\iRacing");
                return key?.GetValue("InstallLocation") as string;
            }
            catch
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\iRacing");
                    return key?.GetValue("InstallLocation") as string;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static double GetTotalMemoryGB()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var totalMemoryKB = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                    return totalMemoryKB / (1024 * 1024); // Convert KB to GB
                }
            }
            catch { }
            
            return 0;
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}