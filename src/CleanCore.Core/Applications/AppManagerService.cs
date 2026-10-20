using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using Microsoft.Win32;

namespace CleanCore.Core.Applications;

public interface IAppManagerService
{
    Task<List<AppInventoryItem>> GetInstalledApplicationsAsync(CancellationToken cancellationToken);
    Task MeasureAppFolderSizesAsync(List<AppInventoryItem> items, IProgress<int>? progress, CancellationToken cancellationToken);
    bool LaunchUninstaller(AppInventoryItem item, out string error);
    bool OpenInstallationFolder(AppInventoryItem item, out string error);
}

public class AppManagerService : IAppManagerService
{
    public Task<List<AppInventoryItem>> GetInstalledApplicationsAsync(CancellationToken cancellationToken)
    {
        var list = new Dictionary<string, AppInventoryItem>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan 64-bit HKLM
        ScanRegistryKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall", list, cancellationToken);

        // 2. Scan 32-bit HKLM (Wow6432Node)
        ScanRegistryKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall", list, cancellationToken);

        // 3. Scan HKCU
        ScanRegistryKey(RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall", list, cancellationToken);

        // Sort by estimated size descending, then by name
        var sorted = list.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
            .OrderByDescending(a => a.TotalFootprintBytes)
            .ThenBy(a => a.DisplayName)
            .ToList();

        return Task.FromResult(sorted);
    }

    public async Task MeasureAppFolderSizesAsync(List<AppInventoryItem> items, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        int processed = 0;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Measure installation directory if known
                if (!string.IsNullOrWhiteSpace(item.InstallLocation) && Directory.Exists(item.InstallLocation))
                {
                    try
                    {
                        var dir = new DirectoryInfo(item.InstallLocation);
                        long size = 0;
                        foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
                        {
                            try { size += f.Length; } catch { }
                        }
                        item.MeasuredFolderSizeBytes = size;
                        item.IsMeasured = true;
                    }
                    catch { }
                }

                // Check for associated caches in AppData/Local and AppData/Roaming matching app name or publisher
                try
                {
                    long cacheSize = 0;
                    string sanitizedName = SanitizeForFolderName(item.DisplayName);
                    if (!string.IsNullOrWhiteSpace(sanitizedName))
                    {
                        string localTarget = Path.Combine(localAppData, sanitizedName);
                        if (Directory.Exists(localTarget))
                        {
                            cacheSize += CalculateSafeDirSize(localTarget);
                        }

                        string roamingTarget = Path.Combine(appData, sanitizedName);
                        if (Directory.Exists(roamingTarget))
                        {
                            cacheSize += CalculateSafeDirSize(roamingTarget);
                        }
                    }
                    item.AssociatedCacheSizeBytes = cacheSize;
                }
                catch { }

                processed++;
                progress?.Report((int)((double)processed / items.Count * 100.0));
            }
        }, cancellationToken);
    }

    public bool LaunchUninstaller(AppInventoryItem item, out string error)
    {
        error = string.Empty;

        string cmd = !string.IsNullOrWhiteSpace(item.UninstallString) ? item.UninstallString : item.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(cmd))
        {
            error = "No uninstaller registered for this application.";
            return false;
        }

        try
        {
            cmd = cmd.Trim();
            string fileName;
            string arguments = string.Empty;

            if (cmd.StartsWith("\""))
            {
                int nextQuote = cmd.IndexOf('\"', 1);
                if (nextQuote > 1)
                {
                    fileName = cmd.Substring(1, nextQuote - 1);
                    arguments = cmd.Substring(nextQuote + 1).Trim();
                }
                else
                {
                    fileName = cmd.Trim('\"');
                }
            }
            else
            {
                int firstSpace = cmd.IndexOf(' ');
                if (firstSpace > 0)
                {
                    fileName = cmd.Substring(0, firstSpace);
                    arguments = cmd.Substring(firstSpace + 1).Trim();
                }
                else
                {
                    fileName = cmd;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not launch uninstaller: {ex.Message}";
            return false;
        }
    }

    public bool OpenInstallationFolder(AppInventoryItem item, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(item.InstallLocation) || !Directory.Exists(item.InstallLocation))
        {
            error = "Installation location is not specified or does not exist.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.InstallLocation,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not open folder: {ex.Message}";
            return false;
        }
    }

    private static void ScanRegistryKey(RegistryKey rootKey, string subKeyPath, Dictionary<string, AppInventoryItem> list, CancellationToken ct)
    {
        try
        {
            using var uninstallKey = rootKey.OpenSubKey(subKeyPath);
            if (uninstallKey == null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey == null) continue;

                    string? displayName = appKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Filter out internal system updates or hotfixes
                    int isSystemComponent = Convert.ToInt32(appKey.GetValue("SystemComponent") ?? 0);
                    string? parentKeyName = appKey.GetValue("ParentKeyName") as string;
                    if (isSystemComponent == 1 || !string.IsNullOrWhiteSpace(parentKeyName)) continue;

                    string? publisher = appKey.GetValue("Publisher") as string ?? string.Empty;
                    string? displayVersion = appKey.GetValue("DisplayVersion") as string ?? string.Empty;
                    string? installLocation = appKey.GetValue("InstallLocation") as string ?? string.Empty;
                    string? installDate = appKey.GetValue("InstallDate") as string ?? string.Empty;
                    string? uninstallString = appKey.GetValue("UninstallString") as string ?? string.Empty;
                    string? quietUninstallString = appKey.GetValue("QuietUninstallString") as string ?? string.Empty;

                    long estimatedSize = 0;
                    object? sizeObj = appKey.GetValue("EstimatedSize");
                    if (sizeObj != null)
                    {
                        // Registry EstimatedSize is stored in KB
                        long sizeKb = Convert.ToInt64(sizeObj);
                        estimatedSize = sizeKb * 1024;
                    }

                    string key = $"{displayName}_{displayVersion}";
                    if (!list.ContainsKey(key))
                    {
                        list[key] = new AppInventoryItem
                        {
                            DisplayName = displayName,
                            Publisher = publisher,
                            DisplayVersion = displayVersion,
                            InstallLocation = installLocation,
                            InstallDate = installDate,
                            UninstallString = uninstallString,
                            QuietUninstallString = quietUninstallString,
                            RegistryKeyPath = $@"{rootKey.Name}\{subKeyPath}\{subKeyName}",
                            RegisteredSizeBytes = estimatedSize,
                            IsSystemComponent = false
                        };
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string SanitizeForFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c))).Trim();
    }

    private static long CalculateSafeDirSize(string path)
    {
        long size = 0;
        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try { size += f.Length; } catch { }
            }
        }
        catch { }
        return size;
    }
}
