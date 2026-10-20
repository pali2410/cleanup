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

public sealed partial class ApplicationManagerPage : Page
{
    private readonly List<AppInventoryItem> _allApps = new();
    public ObservableCollection<AppInventoryItem> DisplayedApps { get; } = new();

    public ApplicationManagerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadApplications();
    }

    private void LoadApplications()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        AppsListView.Visibility = Visibility.Collapsed;
        LoadingText.Text = "Scanning Windows Registry and installed apps...";

        Task.Run(async () =>
        {
            try
            {
                var apps = await AppServices.Current.Apps.GetInstalledApplicationsAsync(CancellationToken.None);

                DispatcherQueue.TryEnqueue(() =>
                {
                    _allApps.Clear();
                    _allApps.AddRange(apps);
                    ApplyFilter();

                    AppCountText.Text = $"{_allApps.Count} applications detected";
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    AppsListView.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingText.Text = $"Error: {ex.Message}";
                });
            }
        });
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = _allApps.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           a.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        DisplayedApps.Clear();
        foreach (var a in filtered)
        {
            DisplayedApps.Add(a);
        }

        AppsListView.ItemsSource = DisplayedApps;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void OnRefreshAppsClick(object sender, RoutedEventArgs e) => LoadApplications();

    private void OnMeasureFoldersClick(object sender, RoutedEventArgs e)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Measuring actual directory sizes on disk...";

        var progress = new Progress<int>(pct =>
        {
            LoadingText.Text = $"Measuring directory footprints ({pct}% complete)...";
        });

        Task.Run(async () =>
        {
            await AppServices.Current.Apps.MeasureAppFolderSizesAsync(_allApps, progress, CancellationToken.None);

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyFilter();
                LoadingPanel.Visibility = Visibility.Collapsed;
            });
        });
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is AppInventoryItem app)
        {
            if (!AppServices.Current.Apps.OpenInstallationFolder(app, out string err))
            {
                var dialog = new ContentDialog
                {
                    Title = "Cannot Open Folder",
                    Content = err,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
        }
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is AppInventoryItem app)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "Launch Uninstaller?",
                Content = $"This will launch the official uninstaller for '{app.DisplayName}'.\n\nCommand: {app.UninstallString}",
                PrimaryButtonText = "Launch Uninstaller",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!AppServices.Current.Apps.LaunchUninstaller(app, out string err))
                {
                    var errDialog = new ContentDialog
                    {
                        Title = "Uninstaller Error",
                        Content = err,
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                }
            }
        }
    }
}
