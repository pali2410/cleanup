using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CleanCore.Core.Data;
using CleanCore.Core.Models;

namespace CleanCore.Core.Settings;

public class AppSettings
{
    public string Theme { get; set; } = "Dark"; // "Dark", "Light", "System"
    public int ScanConcurrency { get; set; } = 4;
    public List<string> ExcludedDirectories { get; set; } = new()
    {
        @"C:\Windows",
        @"C:\Recovery",
        @"C:\System Volume Information"
    };
    public List<string> ExcludedExtensions { get; set; } = new();
    public long DefaultLargeFileThresholdBytes { get; set; } = 500 * 1024 * 1024; // 500 MB
    public bool EnableQuarantineByDefault { get; set; } = true;
    public int QuarantineRetentionDays { get; set; } = 30;
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string SelectedModel { get; set; } = "llama3:latest";
    public AiAutonomyLevel AutonomyLevel { get; set; } = AiAutonomyLevel.SemiAutonomous;
    public double CriticalSpaceThresholdPercent { get; set; } = 5.0;
    public double WarningSpaceThresholdPercent { get; set; } = 10.0;
    public bool AutoCheckDrivesOnStartup { get; set; } = true;
}

public interface IAppSettingsService
{
    AppSettings CurrentSettings { get; }
    Task LoadSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
}

public class AppSettingsService : IAppSettingsService
{
    private readonly ICleanCoreDatabase _database;
    private const string SettingsKey = "CleanCore_Main_Settings_V1";

    public AppSettings CurrentSettings { get; private set; } = new();

    public AppSettingsService(ICleanCoreDatabase database)
    {
        _database = database;
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            var json = await _database.GetSettingAsync(SettingsKey);
            if (!string.IsNullOrEmpty(json))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    CurrentSettings = loaded;
                }
            }
        }
        catch { }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        CurrentSettings = settings;
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await _database.SaveSettingAsync(SettingsKey, json);
        }
        catch { }
    }
}
