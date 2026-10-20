using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;

namespace CleanCore.Core.Cache;

public interface ICacheProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    string Category { get; } // "Windows & System", "Browsers", "Developer", "Applications"
    string Description { get; }
    CleanupRiskLevel RiskLevel { get; }
    bool RequiresElevation { get; }

    bool IsInstalled();
    Task<CacheProviderResult> AnalyzeAsync(CancellationToken cancellationToken);
    Task<CleanupJobResult> CleanAsync(List<CacheTargetItem> targets, bool moveToQuarantine, CancellationToken cancellationToken);
}
