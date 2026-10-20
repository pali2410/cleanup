using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Duplicates;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class DuplicateFinderPage : Page
{
    private CancellationTokenSource? _scanCts;
    public ObservableCollection<DuplicateGroup> GroupsList { get; } = new();

    public DuplicateFinderPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        TargetPathTextBox.Text = Directory.Exists(downloads) ? downloads : "C:\\";
    }

    private void OnScanDuplicatesClick(object sender, RoutedEventArgs e)
    {
        string path = TargetPathTextBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            var err = new ContentDialog
            {
                Title = "Directory Not Found",
                Content = $"The path '{path}' does not exist.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            _ = err.ShowAsync();
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        GroupsItemsControl.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;

        var progress = new Progress<ScanProgressReport>(r =>
        {
            LoadingText.Text = r.CurrentPhase;
            AppServices.Current.ActiveOperation.Update("Duplicate Finder", r.CurrentDirectory, r.FilesDiscovered, r.BytesScanned, r.EstimatedPercent ?? 50.0);
        });

        Task.Run(async () =>
        {
            try
            {
                int concurrency = AppServices.Current.Settings.CurrentSettings.ScanConcurrency;
                var groups = await AppServices.Current.Duplicates.FindDuplicatesAsync(path, 1024, concurrency, progress, ct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    GroupsList.Clear();
                    foreach (var g in groups)
                    {
                        GroupsList.Add(g);
                    }

                    GroupsItemsControl.ItemsSource = GroupsList;
                    UpdateReclaimableDisplay();

                    LoadingPanel.Visibility = Visibility.Collapsed;
                    GroupsItemsControl.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Collapsed;
                    AppServices.Current.ActiveOperation.Complete($"Found {groups.Count} duplicate groups");
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    GroupsItemsControl.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingText.Text = $"Error: {ex.Message}";
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
        }, ct);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void OnKeepOldestClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.Duplicates.ApplyRetentionRule(GroupsList.ToList(), DuplicateRetentionRule.KeepOldest);
        RefreshList();
    }

    private void OnKeepNewestClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.Duplicates.ApplyRetentionRule(GroupsList.ToList(), DuplicateRetentionRule.KeepNewest);
        RefreshList();
    }

    private void OnKeepShortestPathClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.Duplicates.ApplyRetentionRule(GroupsList.ToList(), DuplicateRetentionRule.KeepShortestPath);
        RefreshList();
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        AppServices.Current.Duplicates.ApplyRetentionRule(GroupsList.ToList(), DuplicateRetentionRule.DeselectAll);
        RefreshList();
    }

    private void RefreshList()
    {
        var temp = GroupsList.ToList();
        GroupsList.Clear();
        foreach (var item in temp) GroupsList.Add(item);
        UpdateReclaimableDisplay();
    }

    private void UpdateReclaimableDisplay()
    {
        long totalReclaimable = GroupsList.Sum(g => g.ReclaimableBytes);
        ReclaimableText.Text = $"Reclaimable: {DriveModel.FormatBytes(totalReclaimable)}";
    }

    private void OnItemCheckClicked(object sender, RoutedEventArgs e)
    {
        UpdateReclaimableDisplay();
    }

    private async void OnDeleteDuplicatesClick(object sender, RoutedEventArgs e)
    {
        var selectedToDelete = new List<DuplicateItem>();
        foreach (var g in GroupsList)
        {
            selectedToDelete.AddRange(g.Items.Where(i => i.IsSelectedForDeletion));
        }

        if (selectedToDelete.Count == 0)
        {
            var noDialog = new ContentDialog
            {
                Title = "No Duplicates Selected",
                Content = "Please select duplicate files to delete.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await noDialog.ShowAsync();
            return;
        }

        long totalBytes = selectedToDelete.Sum(i => i.SizeBytes);

        var confirmDialog = new ContentDialog
        {
            Title = "Confirm Duplicate Deletion",
            Content = $"Permanently remove {selectedToDelete.Count} redundant duplicate copies?\n\nEstimated space recovery: {DriveModel.FormatBytes(totalBytes)}\n\nAt least one original copy in each group is preserved.",
            PrimaryButtonText = "Delete Duplicates",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

        long recovered = 0;
        int deletedCount = 0;

        foreach (var item in selectedToDelete)
        {
            if (AppServices.Current.LargeFiles.SafeDeleteFile(item.FullPath, out _))
            {
                recovered += item.SizeBytes;
                deletedCount++;
            }
        }

        DuplicateOutcomeBar.Title = "Duplicate Cleanup Finished";
        DuplicateOutcomeBar.Message = $"Deleted {deletedCount} redundant files, saving {DriveModel.FormatBytes(recovered)}.";
        DuplicateOutcomeBar.Severity = InfoBarSeverity.Success;
        DuplicateOutcomeBar.IsOpen = true;

        // Re-scan
        OnScanDuplicatesClick(sender, e);
    }
}
