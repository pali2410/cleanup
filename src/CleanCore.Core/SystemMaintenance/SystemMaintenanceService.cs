using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Security;

namespace CleanCore.Core.SystemMaintenance;

public record SystemMaintenanceTask(
    string Id,
    string Title,
    string Description,
    bool RequiresElevation,
    string UtilityCommand,
    string UtilityArgs,
    CleanupRiskLevel RiskLevel);

public class SystemMaintenanceService
{
    public IReadOnlyList<SystemMaintenanceTask> AvailableTasks { get; } = new List<SystemMaintenanceTask>
    {
        new(
            "task_dism_cleanup",
            "Windows Component Store Maintenance (DISM)",
            "Cleans up superseded Windows update components and service packs using the official Windows DISM servicing tool.",
            true,
            "dism.exe",
            "/Online /Cleanup-Image /StartComponentCleanup",
            CleanupRiskLevel.Safe),

        new(
            "task_update_cache",
            "Windows Update Download Cache",
            "Removes already-installed Windows Update patch installation files from SoftwareDistribution\\Download.",
            true,
            "internal:clean_update_cache",
            "",
            CleanupRiskLevel.Safe),

        new(
            "task_delivery_opt",
            "Delivery Optimization Cache",
            "Clears peer-to-peer Windows Update delivery optimization chunks cached on the local drive.",
            true,
            "internal:clean_delivery_opt",
            "",
            CleanupRiskLevel.Safe),

        new(
            "task_storage_sense",
            "Windows Storage Sense",
            "Opens the Windows 11 Storage Sense configuration to automate cloud-backed and temp file cleanup.",
            false,
            "explorer.exe",
            "ms-settings:storagesense",
            CleanupRiskLevel.Safe)
    };

    public async Task<CleanupTargetResult> RunTaskAsync(SystemMaintenanceTask task, CancellationToken cancellationToken)
    {
        var result = new CleanupTargetResult
        {
            CandidateId = task.Id,
            Path = task.Title
        };

        if (task.UtilityCommand == "explorer.exe")
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = task.UtilityArgs,
                    UseShellExecute = true
                });
                result.Success = true;
                result.ErrorMessage = "Launched Windows Storage Sense settings.";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        // Check if elevated or if helper is required
        bool isElevated = IsRunningAsAdministrator();

        if (task.Id == "task_dism_cleanup")
        {
            return await RunDismCleanupAsync(isElevated, cancellationToken);
        }

        if (task.Id == "task_update_cache")
        {
            return await CleanWindowsUpdateCacheAsync(isElevated, cancellationToken);
        }

        if (task.Id == "task_delivery_opt")
        {
            return await CleanDeliveryOptimizationAsync(isElevated, cancellationToken);
        }

        result.Success = false;
        result.ErrorMessage = "Unknown maintenance task.";
        return result;
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> GrantDirectoryAccessAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        bool isElevated = IsRunningAsAdministrator();
        if (isElevated)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1. Take ownership if necessary
                    var takeownPsi = new ProcessStartInfo
                    {
                        FileName = "takeown.exe",
                        Arguments = $"/F \"{folderPath}\" /A /D Y",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    try { using var p = Process.Start(takeownPsi); p?.WaitForExit(3000); } catch { }

                    // 2. Grant Read & Execute to BUILTIN\\Administrators (*S-1-5-32-544)
                    var icaclsAdminPsi = new ProcessStartInfo
                    {
                        FileName = "icacls.exe",
                        Arguments = $"\"{folderPath}\" /grant \"*S-1-5-32-544:(OI)(CI)RX\" /C /Q",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var pAdmin = Process.Start(icaclsAdminPsi);
                    pAdmin?.WaitForExit(5000);

                    // 3. Grant Read & Execute to Current User
                    string currentUser = Environment.UserName;
                    if (!string.IsNullOrWhiteSpace(currentUser))
                    {
                        var icaclsUserPsi = new ProcessStartInfo
                        {
                            FileName = "icacls.exe",
                            Arguments = $"\"{folderPath}\" /grant \"{currentUser}:(OI)(CI)RX\" /C /Q",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        try { using var pUser = Process.Start(icaclsUserPsi); pUser?.WaitForExit(5000); } catch { }
                    }

                    // Test if readable now
                    try
                    {
                        _ = new DirectoryInfo(folderPath).GetFileSystemInfos();
                        return true;
                    }
                    catch
                    {
                        return pAdmin?.ExitCode == 0;
                    }
                }
                catch
                {
                    return false;
                }
            }, ct);
        }
        else
        {
            var res = await InvokeElevatedHelperAsync("grant-access", folderPath, ct);
            return res.Success;
        }
    }

    private static async Task<CleanupTargetResult> RunDismCleanupAsync(bool isElevated, CancellationToken ct)
    {
        var res = new CleanupTargetResult { Path = "DISM Component Store" };

        var psi = new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
            UseShellExecute = !isElevated, // if not elevated, use shell execute to prompt UAC if runas
            Verb = isElevated ? "" : "runas",
            RedirectStandardOutput = isElevated,
            RedirectStandardError = isElevated,
            CreateNoWindow = isElevated
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                res.Success = false;
                res.ErrorMessage = "Failed to launch dism.exe.";
                return res;
            }

            string output = string.Empty;
            if (isElevated)
            {
                output = await process.StandardOutput.ReadToEndAsync(ct);
            }

            await process.WaitForExitAsync(ct);

            res.Success = process.ExitCode == 0;
            res.ErrorMessage = process.ExitCode == 0 
                ? "DISM component store cleanup completed successfully." 
                : $"DISM completed with exit code {process.ExitCode}. Output: {output}";

            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.ErrorMessage = $"DISM execution error: {ex.Message}";
            return res;
        }
    }

    private static async Task<CleanupTargetResult> CleanWindowsUpdateCacheAsync(bool isElevated, CancellationToken ct)
    {
        var res = new CleanupTargetResult { Path = @"SoftwareDistribution\Download" };
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var downloadDir = Path.Combine(winDir, "SoftwareDistribution", "Download");

        if (!Directory.Exists(downloadDir))
        {
            res.Success = true;
            res.ErrorMessage = "Download cache directory is empty or does not exist.";
            return res;
        }

        if (!isElevated)
        {
            // Invoke elevated helper
            return await InvokeElevatedHelperAsync("clean-windows-update-cache", downloadDir, ct);
        }

        long recovered = 0;
        int failed = 0;

        await Task.Run(() =>
        {
            var dir = new DirectoryInfo(downloadDir);
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = f.Length;
                    f.Delete();
                    recovered += len;
                }
                catch
                {
                    failed++;
                }
            }
        }, ct);

        res.Success = true;
        res.BytesRecovered = recovered;
        res.ErrorMessage = failed > 0 ? $"{failed} files were currently locked by Windows Update service." : "Windows Update cache cleaned successfully.";
        return res;
    }

    private static async Task<CleanupTargetResult> CleanDeliveryOptimizationAsync(bool isElevated, CancellationToken ct)
    {
        var res = new CleanupTargetResult { Path = @"SoftwareDistribution\DeliveryOptimization" };
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var doDir = Path.Combine(winDir, "SoftwareDistribution", "DeliveryOptimization");

        if (!Directory.Exists(doDir))
        {
            res.Success = true;
            res.ErrorMessage = "Delivery optimization cache is empty.";
            return res;
        }

        if (!isElevated)
        {
            return await InvokeElevatedHelperAsync("clean-delivery-optimization", doDir, ct);
        }

        long recovered = 0;
        await Task.Run(() =>
        {
            var dir = new DirectoryInfo(doDir);
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = f.Length;
                    f.Delete();
                    recovered += len;
                }
                catch { }
            }
        }, ct);

        res.Success = true;
        res.BytesRecovered = recovered;
        res.ErrorMessage = "Delivery optimization cache cleaned.";
        return res;
    }

    private static async Task<CleanupTargetResult> InvokeElevatedHelperAsync(string action, string targetPath, CancellationToken ct)
    {
        var res = new CleanupTargetResult { Path = targetPath };
        var helperExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CleanCore.ElevatedHelper.exe");

        if (!File.Exists(helperExe))
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CleanCore.ElevatedHelper.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "CleanCore.ElevatedHelper", "bin", "Release", "net9.0-windows10.0.26100.0", "CleanCore.ElevatedHelper.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "CleanCore.ElevatedHelper", "bin", "Debug", "net9.0-windows10.0.26100.0", "CleanCore.ElevatedHelper.exe"),
                @"E:\cleandisck\src\CleanCore.ElevatedHelper\bin\Release\net9.0-windows10.0.26100.0\CleanCore.ElevatedHelper.exe",
                @"E:\cleandisck\src\CleanCore.ElevatedHelper\bin\Debug\net9.0-windows10.0.26100.0\CleanCore.ElevatedHelper.exe"
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    helperExe = c;
                    break;
                }
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperExe,
            Arguments = $"--action \"{action}\" --target \"{targetPath}\"",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                res.Success = false;
                res.ErrorMessage = "User cancelled or UAC prompt failed.";
                return res;
            }

            await proc.WaitForExitAsync(ct);
            res.Success = proc.ExitCode == 0;
            res.ErrorMessage = proc.ExitCode == 0 ? "Elevated maintenance completed successfully." : $"Elevated task exited with code {proc.ExitCode}.";
            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.ErrorMessage = $"Elevation request error: {ex.Message}";
            return res;
        }
    }
}
