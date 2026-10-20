using System;
using System.IO;
using System.Text.Json;
using CleanCore.Core.Security;

namespace CleanCore.ElevatedHelper;

public class Program
{
    public static int Main(string[] args)
    {
        string action = string.Empty;
        string target = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--action" && i + 1 < args.Length)
            {
                action = args[++i];
            }
            else if (args[i] == "--target" && i + 1 < args.Length)
            {
                target = args[++i];
            }
        }

        LogAudit($"[START] Requested action: '{action}', Target: '{target}'");

        if (string.IsNullOrWhiteSpace(action))
        {
            LogAudit("[REJECTED] Action argument missing.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "Action parameter is required." }));
            return 1;
        }

        // Validate deletion targets against PathSafetyGuard
        if (!string.IsNullOrWhiteSpace(target) && !string.Equals(action, "grant-access", StringComparison.OrdinalIgnoreCase))
        {
            if (!PathSafetyGuard.IsPathSafeForDeletion(target, out string safetyError))
            {
                LogAudit($"[REJECTED] Path safety violation on '{target}': {safetyError}");
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = safetyError }));
                return 2;
            }
        }

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "grant-access":
                {
                    CleanCore.Core.Storage.NativeStorageMethods.EnableBackupPrivileges();
                    if (!Directory.Exists(target))
                    {
                        LogAudit($"[REJECTED] Directory does not exist '{target}'");
                        Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "Directory does not exist." }));
                        return 2;
                    }

                    try
                    {
                        var takeownPsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "takeown.exe",
                            Arguments = $"/F \"{target}\" /A /D Y",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = System.Diagnostics.Process.Start(takeownPsi);
                        p?.WaitForExit(4000);
                    }
                    catch { }

                    var icaclsAdminPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "icacls.exe",
                        Arguments = $"\"{target}\" /grant \"*S-1-5-32-544:(OI)(CI)RX\" /C /Q",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = System.Diagnostics.Process.Start(icaclsAdminPsi))
                    {
                        p?.WaitForExit(5000);
                    }

                    string currentUser = Environment.UserName;
                    if (!string.IsNullOrWhiteSpace(currentUser))
                    {
                        try
                        {
                            var icaclsUserPsi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "icacls.exe",
                                Arguments = $"\"{target}\" /grant \"{currentUser}:(OI)(CI)RX\" /C /Q",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var p = System.Diagnostics.Process.Start(icaclsUserPsi);
                            p?.WaitForExit(5000);
                        }
                        catch { }
                    }

                    LogAudit($"[SUCCESS] Granted read access to '{target}' for Administrators and {currentUser}");
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, bytesRecovered = 0, message = "Access granted" }));
                    return 0;
                }

                case "clean-system-temp":
                {
                    string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                    long recovered = CleanDirectoryFilesOlderThan(winTemp, DateTime.Now.AddHours(-24));
                    LogAudit($"[SUCCESS] Cleaned system temp. Bytes recovered: {recovered}");
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, bytesRecovered = recovered }));
                    return 0;
                }

                case "clean-windows-update-cache":
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string updateDir = Path.Combine(winDir, "SoftwareDistribution", "Download");
                    long recovered = CleanDirectoryContents(updateDir);
                    LogAudit($"[SUCCESS] Cleaned Windows Update download cache. Bytes recovered: {recovered}");
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, bytesRecovered = recovered }));
                    return 0;
                }

                case "clean-delivery-optimization":
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string doDir = Path.Combine(winDir, "SoftwareDistribution", "DeliveryOptimization");
                    long recovered = CleanDirectoryContents(doDir);
                    LogAudit($"[SUCCESS] Cleaned Delivery Optimization cache. Bytes recovered: {recovered}");
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, bytesRecovered = recovered }));
                    return 0;
                }

                default:
                    LogAudit($"[REJECTED] Unsupported action '{action}'.");
                    Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = $"Unsupported elevated action: {action}" }));
                    return 3;
            }
        }
        catch (Exception ex)
        {
            LogAudit($"[ERROR] Execution failure: {ex.Message}");
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.Message }));
            return 4;
        }
    }

    private static long CleanDirectoryContents(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return 0;
        long recovered = 0;
        var dir = new DirectoryInfo(dirPath);
        foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
        {
            if (PathSafetyGuard.IsFileLocked(f.FullName, out _)) continue;
            try
            {
                long len = f.Length;
                f.Delete();
                recovered += len;
            }
            catch { }
        }
        return recovered;
    }

    private static long CleanDirectoryFilesOlderThan(string dirPath, DateTime cutoff)
    {
        if (!Directory.Exists(dirPath)) return 0;
        long recovered = 0;
        var dir = new DirectoryInfo(dirPath);
        foreach (var f in dir.GetFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (f.LastWriteTime >= cutoff) continue;
            if (PathSafetyGuard.IsFileLocked(f.FullName, out _)) continue;
            try
            {
                long len = f.Length;
                f.Delete();
                recovered += len;
            }
            catch { }
        }
        return recovered;
    }

    private static void LogAudit(string message)
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CleanCore", "Logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "elevated_audit.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";
            File.AppendAllText(logFile, entry);
        }
        catch { }
    }
}
