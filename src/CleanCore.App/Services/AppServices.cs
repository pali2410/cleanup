using System;
using System.IO;
using System.Threading.Tasks;
using CleanCore.Core.Ai;
using CleanCore.Core.Applications;
using CleanCore.Core.Cache;
using CleanCore.Core.Cleanup;
using CleanCore.Core.Data;
using CleanCore.Core.Duplicates;
using CleanCore.Core.LargeFiles;
using CleanCore.Core.Models;
using CleanCore.Core.Quarantine;
using CleanCore.Core.Settings;
using CleanCore.Core.Storage;
using CleanCore.Core.SystemMaintenance;

namespace CleanCore_App.Services;

public class ActiveOperationState
{
    public bool IsActive { get; set; }
    public string OperationName { get; set; } = "Idle";
    public string CurrentPath { get; set; } = string.Empty;
    public long ItemsCount { get; set; }
    public long BytesProcessed { get; set; }
    public double ProgressPercentage { get; set; }
    public event Action? OnStateChanged;

    public void Update(string name, string path, long count, long bytes, double percent)
    {
        IsActive = true;
        OperationName = name;
        CurrentPath = path;
        ItemsCount = count;
        BytesProcessed = bytes;
        ProgressPercentage = percent;
        OnStateChanged?.Invoke();
    }

    public void Complete(string message)
    {
        IsActive = false;
        OperationName = message;
        ProgressPercentage = 100;
        OnStateChanged?.Invoke();
    }
}

public class AppServices
{
    private static AppServices? _instance;
    public static AppServices Current => _instance ??= new AppServices();

    public IDriveService Drives { get; }
    public IDirectoryScanner Scanner { get; }
    public ILargeFileManager LargeFiles { get; }
    public IDuplicateFinderService Duplicates { get; }
    public ICacheEngine Cache { get; }
    public IAppManagerService Apps { get; }
    public SystemMaintenanceService SystemMaintenance { get; }
    public IQuarantineManager Quarantine { get; }
    public ICleanCoreDatabase Database { get; }
    public IOllamaClient Ollama { get; }
    public ToolRegistry Tools { get; }
    public ICleanupExecutionEngine Cleanup { get; }
    public IAppSettingsService Settings { get; }
    public ActiveOperationState ActiveOperation { get; } = new();

    // Shared state caches for cross-page workflow
    public StorageNode? LastScannedRoot { get; set; }
    public CleanupPlan? CurrentCleanupPlan { get; set; }

    public AppServices()
    {
        Drives = new DriveService();
        Scanner = new DirectoryScanner();
        LargeFiles = new LargeFileManager();
        Duplicates = new DuplicateFinderService();
        Cache = new CacheEngine();
        Apps = new AppManagerService();
        SystemMaintenance = new SystemMaintenanceService();
        Quarantine = new QuarantineManager();
        Database = new CleanCoreDatabase();
        Ollama = new OllamaClient();
        Cleanup = new CleanupExecutionEngine(Quarantine, Drives);
        Settings = new AppSettingsService(Database);

        Tools = new ToolRegistry(
            Drives,
            Scanner,
            LargeFiles,
            Duplicates,
            Cache,
            Apps,
            Cleanup);
    }

    public async Task InitializeAsync()
    {
        NativeStorageMethods.EnableBackupPrivileges();
        await Database.InitializeAsync();
        await Settings.LoadSettingsAsync();
    }
}
