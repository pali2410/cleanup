using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CleanCore.Core.Models;

public enum CleanupRiskLevel
{
    Safe,       // Very low risk: pure temp, thumbnails, obsolete logs
    Moderate,   // Caches that might require redownload or recompile
    High        // User downloads, sensitive browser data, direct deletes
}

public class CacheTargetItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? LastModified { get; set; }
    public bool IsLocked { get; set; }
    public string LockProcessName { get; set; } = string.Empty;
    public string SideEffectDescription { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string SizeDisplay => DriveModel.FormatBytes(SizeBytes);
    public string ModifiedDisplay => LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
}

public class CacheProviderResult : INotifyPropertyChanged
{
    private bool? _isSelected = true;
    private bool _isSynchronizing;

    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // System, Browser, Developer, Application
    public string Description { get; set; } = string.Empty;
    public CleanupRiskLevel RiskLevel { get; set; } = CleanupRiskLevel.Safe;
    public bool RequiresElevation { get; set; }
    public bool IsInstalled { get; set; } = true;
    public bool IsRunning { get; set; }
    public string RunningProcessWarning { get; set; } = string.Empty;
    public List<CacheTargetItem> Targets { get; set; } = new();
    public long TotalSizeBytes { get; set; }
    public int TotalItemCount => Targets.Count;

    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();

                // If user toggles the provider, cascade to children
                if (!_isSynchronizing && value.HasValue)
                {
                    _isSynchronizing = true;
                    foreach (var target in Targets)
                    {
                        if (!target.IsLocked)
                        {
                            target.IsSelected = value.Value;
                        }
                    }
                    _isSynchronizing = false;
                }

                OnPropertyChanged(nameof(SelectedSizeBytes));
                OnPropertyChanged(nameof(SelectedSizeDisplay));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public long SelectedSizeBytes => Targets.Where(t => t.IsSelected).Sum(t => t.SizeBytes);
    public string SelectedSizeDisplay => DriveModel.FormatBytes(SelectedSizeBytes);

    public void SyncSelectionFromChildren()
    {
        if (_isSynchronizing) return;

        _isSynchronizing = true;
        int unlockedCount = Targets.Count(t => !t.IsLocked);
        int selectedCount = Targets.Count(t => t.IsSelected);

        if (Targets.Count == 0 || (selectedCount == unlockedCount && unlockedCount > 0))
        {
            _isSelected = true;
        }
        else if (selectedCount == 0)
        {
            _isSelected = false;
        }
        else
        {
            _isSelected = null; // Indeterminate
        }

        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSizeDisplay));
        _isSynchronizing = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterChildEvents()
    {
        foreach (var target in Targets)
        {
            target.SelectionChanged -= OnChildSelectionChanged;
            target.SelectionChanged += OnChildSelectionChanged;
        }
        SyncSelectionFromChildren();
    }

    private void OnChildSelectionChanged(object? sender, EventArgs e)
    {
        SyncSelectionFromChildren();
    }

    public event EventHandler? SelectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string SizeDisplay => DriveModel.FormatBytes(TotalSizeBytes);
    public string RiskBadge => RiskLevel switch
    {
        CleanupRiskLevel.Safe => "Safe",
        CleanupRiskLevel.Moderate => "Moderate Risk",
        CleanupRiskLevel.High => "High Risk / Caution",
        _ => "Standard"
    };
}
