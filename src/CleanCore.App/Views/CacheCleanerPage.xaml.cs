using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class CacheCleanerPage : Page
{
    private CancellationTokenSource? _scanCts;
    public ObservableCollection<CacheProviderResult> ProvidersList { get; } = new();

    public CacheCleanerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScanCaches();
    }

    private void ScanCaches()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        ProvidersItemsControl.Visibility = Visibility.Collapsed;
        CleanSelectedButton.IsEnabled = false;

        var progress = new Progress<ScanProgressReport>(r =>
        {
            LoadingText.Text = r.CurrentPhase;
        });

        Task.Run(async () =>
        {
            try
            {
                var results = await AppServices.Current.Cache.AnalyzeAllAsync(progress, ct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    ProvidersList.Clear();
                    bool anyBrowserRunning = false;

                    foreach (var res in results)
                    {
                        res.RegisterChildEvents();
                        res.SelectionChanged += (s, e) => RecalculateTotals();
                        ProvidersList.Add(res);
                        if (res.IsRunning) anyBrowserRunning = true;
                    }

                    ProvidersItemsControl.ItemsSource = ProvidersList;
                    ProvidersCountText.Text = $"{results.Count} Providers";
                    RunningBrowserWarning.IsOpen = anyBrowserRunning;

                    RecalculateTotals();

                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ProvidersItemsControl.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingText.Text = $"Error: {ex.Message}";
                });
            }
        }, ct);
    }

    private void OnScanCachesClick(object sender, RoutedEventArgs e) => ScanCaches();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in ProvidersList)
        {
            p.IsSelected = true;
        }
        RecalculateTotals();
    }

    private void OnSelectSafeOnlyClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in ProvidersList)
        {
            p.IsSelected = p.RiskLevel == CleanupRiskLevel.Safe;
        }
        RecalculateTotals();
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in ProvidersList)
        {
            p.IsSelected = false;
        }
        RecalculateTotals();
    }

    private void OnProviderCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is CacheProviderResult provider)
        {
            bool newSelection = cb.IsChecked == true;
            provider.IsSelected = newSelection;
        }
        RecalculateTotals();
    }

    private void OnTargetCheckBoxClick(object sender, RoutedEventArgs e)
    {
        RecalculateTotals();
    }

    private void RecalculateTotals()
    {
        long selectedBytes = 0;
        int selectedTargetsCount = 0;

        foreach (var p in ProvidersList)
        {
            var targets = p.Targets.Where(t => t.IsSelected).ToList();
            selectedBytes += targets.Sum(t => t.SizeBytes);
            selectedTargetsCount += targets.Count;
        }

        TotalReclaimableText.Text = DriveModel.FormatBytes(selectedBytes);
        TotalReclaimableText.Foreground = selectedBytes > 0
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        CleanSelectedButton.IsEnabled = selectedTargetsCount > 0;
    }

    private async void OnCleanSelectedClick(object sender, RoutedEventArgs e)
    {
        var targetsToClean = new List<CacheTargetItem>();
        foreach (var p in ProvidersList)
        {
            targetsToClean.AddRange(p.Targets.Where(t => t.IsSelected));
        }

        if (targetsToClean.Count == 0)
        {
            var noItemsDialog = new ContentDialog
            {
                Title = "No Items Selected",
                Content = "Please select at least one cache provider or target item to clean.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await noItemsDialog.ShowAsync();
            return;
        }

        long expectedRecovery = targetsToClean.Sum(t => t.SizeBytes);

        var confirmDialog = new ContentDialog
        {
            Title = "Confirm Cache Cleanup",
            Content = $"Are you sure you want to clean {targetsToClean.Count} selected cache targets?\n\nEstimated space recovery: {DriveModel.FormatBytes(expectedRecovery)}\n\nThis will remove scratch files, browser page caches, and package archives.",
            PrimaryButtonText = "Clean Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CleanSelectedButton.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Executing safe cache cleanup...";

        bool useQuarantine = AppServices.Current.Settings.CurrentSettings.EnableQuarantineByDefault;

        var result = await AppServices.Current.Cache.CleanTargetsAsync(targetsToClean, useQuarantine, CancellationToken.None);

        // Record in SQLite database history
        await AppServices.Current.Database.RecordCleanupJobAsync(result);

        LoadingPanel.Visibility = Visibility.Collapsed;
        CleanSelectedButton.IsEnabled = true;

        CleanupOutcomeBar.Title = "Cache Cleanup Finished";
        CleanupOutcomeBar.Message = $"Recovered {result.BytesRecoveredDisplay} across {result.SuccessCount} items. (Skipped/Locked: {result.SkippedLockedCount})";
        CleanupOutcomeBar.Severity = InfoBarSeverity.Success;
        CleanupOutcomeBar.IsOpen = true;

        // Re-scan to reflect fresh capacity
        ScanCaches();
    }
}
