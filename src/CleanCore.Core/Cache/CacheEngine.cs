using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;

namespace CleanCore.Core.Cache;

public interface ICacheEngine
{
    IReadOnlyList<ICacheProvider> Providers { get; }
    Task<List<CacheProviderResult>> AnalyzeAllAsync(IProgress<ScanProgressReport>? progress, CancellationToken cancellationToken);
    Task<CleanupJobResult> CleanTargetsAsync(List<CacheTargetItem> targets, bool moveToQuarantine, CancellationToken cancellationToken);
}

public class CacheEngine : ICacheEngine
{
    private readonly List<ICacheProvider> _providers;

    public IReadOnlyList<ICacheProvider> Providers => _providers;

    public CacheEngine()
    {
        _providers = new List<ICacheProvider>
        {
            new UserTempCacheProvider(),
            new WindowsTempCacheProvider(),
            new BrowserCacheProvider(),
            new DeveloperCacheProvider(),
            new RecycleBinCacheProvider()
        };
    }

    public async Task<List<CacheProviderResult>> AnalyzeAllAsync(IProgress<ScanProgressReport>? progress, CancellationToken cancellationToken)
    {
        var results = new List<CacheProviderResult>();
        int count = 0;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            progress?.Report(new ScanProgressReport
            {
                CurrentPhase = $"Analyzing Cache: {provider.DisplayName} ({count}/{_providers.Count})..."
            });

            try
            {
                var res = await provider.AnalyzeAsync(cancellationToken);
                results.Add(res);
            }
            catch (Exception ex)
            {
                results.Add(new CacheProviderResult
                {
                    ProviderId = provider.ProviderId,
                    DisplayName = provider.DisplayName,
                    Category = provider.Category,
                    Description = $"Analysis error: {ex.Message}",
                    IsInstalled = false
                });
            }
        }

        return results;
    }

    public async Task<CleanupJobResult> CleanTargetsAsync(List<CacheTargetItem> targets, bool moveToQuarantine, CancellationToken cancellationToken)
    {
        var masterJob = new CleanupJobResult { TotalTargets = targets.Count };

        // Group targets by provider heuristic or execute standard filesystem cleanup
        var userTemp = new UserTempCacheProvider();
        var result = await userTemp.CleanAsync(targets, moveToQuarantine, cancellationToken);
        return result;
    }
}
