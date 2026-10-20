using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace CleanCore_App.Views;

public sealed partial class StorageExplorerPage : Page
{
    private string _currentPath = "C:\\";
    private StorageNode? _currentNode;
    private CancellationTokenSource? _scanCts;
    private readonly List<StorageNode> _allItems = new();
    public ObservableCollection<StorageNode> DisplayedItems { get; } = new();

    public StorageExplorerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            _currentPath = path;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateDrives();
        StartScan(deep: false);
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives.GetDrives();
        DriveSelector.Items.Clear();
        int selectedIndex = 0;
        int i = 0;

        foreach (var d in drives)
        {
            var item = new ComboBoxItem { Content = $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel}) - {d.FreeDisplay} Free", Tag = d.Name };
            DriveSelector.Items.Add(item);
            if (string.Equals(Path.GetPathRoot(_currentPath), d.Name, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
            i++;
        }

        if (DriveSelector.Items.Count > 0)
        {
            DriveSelector.SelectedIndex = selectedIndex;
        }
    }

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedItem is ComboBoxItem item && item.Tag is string drivePath)
        {
            if (!string.Equals(_currentPath, drivePath, StringComparison.OrdinalIgnoreCase))
            {
                _currentPath = drivePath;
                StartScan(deep: false);
            }
        }
    }

    private void StartScan(bool deep)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        ItemsListView.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        LoadingStatusText.Text = $"Scanning {_currentPath}...";

        var progress = new Progress<ScanProgressReport>(report =>
        {
            LoadingStatusText.Text = $"{report.CurrentPhase} ({report.FilesDiscovered} files, {report.BytesDisplay})";
            AppServices.Current.ActiveOperation.Update("Storage Explorer", report.CurrentDirectory, report.FilesDiscovered, report.BytesScanned, report.EstimatedPercent ?? 50.0);
        });

        Task.Run(async () =>
        {
            try
            {
                int concurrency = AppServices.Current.Settings.CurrentSettings.ScanConcurrency;
                var root = await AppServices.Current.Scanner.ScanDirectoryAsync(_currentPath, deep, concurrency, progress, ct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    _currentNode = root;
                    AppServices.Current.LastScannedRoot = root;
                    _allItems.Clear();
                    _allItems.AddRange(root.Children);
                    ApplyFilterAndSort();

                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ItemsListView.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Collapsed;
                    AppServices.Current.ActiveOperation.Complete("Directory Scanned");
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ItemsListView.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingStatusText.Text = $"Error: {ex.Message}";
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
        }, ct);
    }

    private void ApplyFilterAndSort()
    {
        if (ItemsListView == null || SearchBox == null || SortSelector == null) return;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = _allItems.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           i.Extension.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        switch (SortSelector.SelectedIndex)
        {
            case 0: // Size desc
                filtered = filtered.OrderByDescending(i => i.SizeBytes);
                break;
            case 1: // Name asc
                filtered = filtered.OrderBy(i => i.Name);
                break;
            case 2: // Modified desc
                filtered = filtered.OrderByDescending(i => i.LastModified ?? DateTime.MinValue);
                break;
        }

        DisplayedItems.Clear();
        foreach (var item in filtered)
        {
            DisplayedItems.Add(item);
        }

        ItemsListView.ItemsSource = DisplayedItems;

        // Check for Access Denied nodes
        bool hasDenied = _allItems.Any(i => i.IsPermissionDenied);
        if (AdminElevationBanner != null)
        {
            if (hasDenied)
            {
                AdminElevationBanner.IsOpen = true;
                bool isAdmin = CleanCore.Core.SystemMaintenance.SystemMaintenanceService.IsRunningAsAdministrator();
                if (isAdmin)
                {
                    AdminElevationBanner.Title = "Protected Windows Folders Detected";
                    AdminElevationBanner.Message = "Certain system folders (such as System Volume Information) require granting read permissions to explore. Click 'Unlock Protected Folders' or double-click to grant access.";
                    AdminActionButton.Content = "Unlock Protected Folders";
                    AdminUnlockButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    AdminElevationBanner.Title = "Administrator Privileges Required";
                    AdminElevationBanner.Message = "Protected folders (PerfLogs, Config.Msi, MSOCache, System Volume Information) are locked. Restart CleanCore as Administrator or unlock them to explore.";
                    AdminActionButton.Content = "Restart as Administrator";
                    AdminUnlockButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                AdminElevationBanner.IsOpen = false;
            }
        }
    }

    private void OnFastScanClick(object sender, RoutedEventArgs e) => StartScan(deep: false);

    private void OnDeepScanClick(object sender, RoutedEventArgs e) => StartScan(deep: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void OnNavigateUpClick(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent != null)
        {
            _currentPath = parent.FullName;
            StartScan(deep: false);
        }
    }

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemsListView.SelectedItem is StorageNode node)
        {
            if (node.IsDirectory)
            {
                // Handle legacy Windows junction (e.g. C:\Documents and Settings -> C:\Users)
                if (node.IsReparsePoint && node.Name.Equals("Documents and Settings", StringComparison.OrdinalIgnoreCase))
                {
                    _currentPath = Path.Combine(Path.GetPathRoot(node.FullPath) ?? "C:\\", "Users");
                    StartScan(deep: false);
                    return;
                }

                if (node.IsPermissionDenied)
                {
                    await HandleAccessDeniedFolderAsync(node);
                    return;
                }

                _currentPath = node.FullPath;
                StartScan(deep: false);
            }
            else
            {
                // Open file with default shell handler
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = node.FullPath, UseShellExecute = true });
                }
                catch { }
            }
        }
    }

    private async Task HandleAccessDeniedFolderAsync(StorageNode node)
    {
        bool isAdmin = CleanCore.Core.SystemMaintenance.SystemMaintenanceService.IsRunningAsAdministrator();

        var dialog = new ContentDialog
        {
            Title = $"Unlock Folder Access: {node.Name}",
            Content = $"Windows security restrictions are preventing access to '{node.FullPath}'.\n\nCleanCore can grant Read permissions to your user and Administrators, or restart the app with full Administrator privileges.",
            PrimaryButtonText = "Grant Access & Open",
            SecondaryButtonText = isAdmin ? "" : "Restart as Administrator",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingStatusText.Text = $"Granting access to {node.Name}...";

            bool success = await CleanCore.Core.SystemMaintenance.SystemMaintenanceService.GrantDirectoryAccessAsync(node.FullPath);
            LoadingPanel.Visibility = Visibility.Collapsed;

            if (success)
            {
                node.IsPermissionDenied = false;
                _currentPath = node.FullPath;
                StartScan(deep: false);
            }
            else
            {
                var failDialog = new ContentDialog
                {
                    Title = "Elevation Required",
                    Content = $"Could not unlock '{node.Name}'. Would you like to restart CleanCore with Administrator privileges?",
                    PrimaryButtonText = "Restart as Admin",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot
                };
                if (await failDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    MainPage.RestartAsAdministrator();
                }
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            MainPage.RestartAsAdministrator();
        }
    }

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is StorageNode node)
        {
            var menu = new MenuFlyout();

            if (node.IsPermissionDenied)
            {
                var unlockItem = new MenuFlyoutItem { Text = "Unlock Access (Grant Permissions)" };
                unlockItem.Click += async (s, args) => await HandleAccessDeniedFolderAsync(node);
                menu.Items.Add(unlockItem);

                if (!CleanCore.Core.SystemMaintenance.SystemMaintenanceService.IsRunningAsAdministrator())
                {
                    var elevateItem = new MenuFlyoutItem { Text = "Restart CleanCore as Administrator" };
                    elevateItem.Click += (s, args) => MainPage.RestartAsAdministrator();
                    menu.Items.Add(elevateItem);
                }
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var openItem = new MenuFlyoutItem { Text = node.IsDirectory ? "Open Folder" : "Open File" };
            openItem.Click += (s, args) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = node.FullPath, UseShellExecute = true }); } catch { }
            };
            menu.Items.Add(openItem);

            var revealItem = new MenuFlyoutItem { Text = "Show in File Explorer" };
            revealItem.Click += (s, args) =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{node.FullPath}\""); } catch { }
            };
            menu.Items.Add(revealItem);

            var copyItem = new MenuFlyoutItem { Text = "Copy Path" };
            copyItem.Click += (s, args) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(node.FullPath);
                Clipboard.SetContent(pkg);
            };
            menu.Items.Add(copyItem);

            menu.ShowAt(fe, e.GetPosition(fe));
        }
    }

    private void OnAdminActionButtonClick(object sender, RoutedEventArgs e)
    {
        bool isAdmin = CleanCore.Core.SystemMaintenance.SystemMaintenanceService.IsRunningAsAdministrator();
        if (isAdmin)
        {
            OnUnlockAccessClick(sender, e);
        }
        else
        {
            MainPage.RestartAsAdministrator();
        }
    }

    private async void OnUnlockAccessClick(object sender, RoutedEventArgs e)
    {
        var deniedNodes = _allItems.Where(i => i.IsPermissionDenied).ToList();
        if (deniedNodes.Count == 0) return;

        LoadingPanel.Visibility = Visibility.Visible;
        LoadingStatusText.Text = $"Unlocking {deniedNodes.Count} system folders...";

        int unlockedCount = 0;
        foreach (var node in deniedNodes)
        {
            bool ok = await CleanCore.Core.SystemMaintenance.SystemMaintenanceService.GrantDirectoryAccessAsync(node.FullPath);
            if (ok) unlockedCount++;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        StartScan(deep: false);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilterAndSort();
    }

    private void OnSortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilterAndSort();
    }

    private void OnCopyCurrentPathClick(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(_currentPath);
        Clipboard.SetContent(pkg);
    }
}
