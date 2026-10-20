using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class LargeFilesPage : Page
{
    private long _currentThresholdBytes = 524288000; // 500 MB
    private CancellationTokenSource? _scanCts;
    private readonly List<LargeFileEntry> _allLargeFiles = new();
    public ObservableCollection<LargeFileEntry> DisplayedFiles { get; } = new();

    public LargeFilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateDrives();
        StartLargeFileScan();
    }

    private void PopulateDrives()
    {
        DriveSelector.Items.Clear();
        var drives = AppServices.Current.Drives.GetDrives();
        foreach (var d in drives)
        {
            DriveSelector.Items.Add(new ComboBoxItem { Content = $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel})", Tag = d.Name });
        }
        if (DriveSelector.Items.Count > 0) DriveSelector.SelectedIndex = 0;
    }

    private void StartLargeFileScan()
    {
        string targetPath = "C:\\";
        if (DriveSelector.SelectedItem is ComboBoxItem item && item.Tag is string d)
        {
            targetPath = d;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        LargeFilesListView.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        LoadingText.Text = $"Finding files >= {DriveModel.FormatBytes(_currentThresholdBytes)} on {targetPath}...";

        var progress = new Progress<ScanProgressReport>(r =>
        {
            LoadingText.Text = $"{r.CurrentPhase} ({r.FilesDiscovered} files examined)";
            AppServices.Current.ActiveOperation.Update("Large Files", r.CurrentDirectory, r.FilesDiscovered, r.BytesScanned, r.EstimatedPercent ?? 50.0);
        });

        Task.Run(async () =>
        {
            try
            {
                int concurrency = AppServices.Current.Settings.CurrentSettings.ScanConcurrency;
                var list = await AppServices.Current.Scanner.FindLargeFilesAsync(targetPath, _currentThresholdBytes, concurrency, progress, ct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    _allLargeFiles.Clear();
                    _allLargeFiles.AddRange(list);
                    ApplyFilter();

                    LoadingPanel.Visibility = Visibility.Collapsed;
                    LargeFilesListView.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Collapsed;
                    AppServices.Current.ActiveOperation.Complete($"Found {_allLargeFiles.Count} large files");
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    LargeFilesListView.Visibility = Visibility.Visible;
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

    private void ApplyFilter()
    {
        if (SearchBox == null || CategoryFilterComboBox == null || LargeFilesListView == null) return;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = _allLargeFiles.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(f => f.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           f.Extension.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (CategoryFilterComboBox.SelectedIndex > 0)
        {
            filtered = CategoryFilterComboBox.SelectedIndex switch
            {
                1 => filtered.Where(f => f.Category == FileCategory.VirtualMachines),
                2 => filtered.Where(f => f.Category == FileCategory.Videos),
                3 => filtered.Where(f => f.Category == FileCategory.Archives),
                4 => filtered.Where(f => f.Category == FileCategory.TemporaryFiles),
                _ => filtered
            };
        }

        DisplayedFiles.Clear();
        foreach (var f in filtered)
        {
            DisplayedFiles.Add(f);
        }

        LargeFilesListView.ItemsSource = DisplayedFiles;
    }

    private void OnThresholdChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string s && long.TryParse(s, out long b))
        {
            _currentThresholdBytes = b;
            if (IsLoaded) StartLargeFileScan();
        }
    }

    private void OnScanLargeFilesClick(object sender, RoutedEventArgs e) => StartLargeFileScan();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async void OnDeleteFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is LargeFileEntry file)
        {
            var dialog = new ContentDialog
            {
                Title = "Permanently Delete Large File?",
                Content = $"Are you sure you want to permanently delete '{file.FileName}' ({file.SizeDisplay})?\n\nPath: {file.FullPath}\n\nThis operation cannot be undone.",
                PrimaryButtonText = "Delete Permanently",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                bool deleted = AppServices.Current.LargeFiles.SafeDeleteFile(file.FullPath, out string msg);
                if (deleted)
                {
                    _allLargeFiles.Remove(file);
                    ApplyFilter();
                }
                else
                {
                    var errDialog = new ContentDialog
                    {
                        Title = "Delete Failed",
                        Content = msg,
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                }
            }
        }
    }

    private async void OnMoveFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is LargeFileEntry file)
        {
            // Input dialog for destination path
            var inputTextBox = new TextBox
            {
                PlaceholderText = @"e.g. D:\ArchivedLargeFiles",
                Text = @"D:\ArchivedFiles"
            };

            var moveDialog = new ContentDialog
            {
                Title = "Safe Relocate File (Copy-Verify-Delete)",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"File: {file.FileName} ({file.SizeDisplay})" },
                        new TextBlock { Text = "Specify the destination folder on another drive. CleanCore will copy the file, verify SHA-256 byte integrity, and only remove the original upon success:" },
                        inputTextBox
                    }
                },
                PrimaryButtonText = "Verify and Move",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var dialogResult = await moveDialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Primary)
            {
                string destination = inputTextBox.Text.Trim();
                var (success, message) = await AppServices.Current.LargeFiles.SafeMoveFileAsync(
                    file.FullPath,
                    destination,
                    null,
                    CancellationToken.None);

                var outcomeDialog = new ContentDialog
                {
                    Title = success ? "Move Succeeded" : "Move Failed",
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await outcomeDialog.ShowAsync();

                if (success)
                {
                    _allLargeFiles.Remove(file);
                    ApplyFilter();
                }
            }
        }
    }
}
