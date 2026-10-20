using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CleanCore.Core.Models;
using CleanCore.Core.SystemMaintenance;
using CleanCore_App.Services;
using CleanCore_App.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanCore_App;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.InitializeAsync();
        UpdateQuickStatus();
        UpdateElevationStatus();

        // Listen for background scan updates in live activity drawer
        AppServices.Current.ActiveOperation.OnStateChanged += OnActiveOperationChanged;

        // Apply saved theme
        var theme = AppServices.Current.Settings.CurrentSettings.Theme;
        if (this.XamlRoot?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme == "Light" ? ElementTheme.Light : ElementTheme.Dark;
        }

        // Navigate to initial page (Overview)
        NavigateToPage("Overview");
    }

    private void UpdateQuickStatus()
    {
        var settings = AppServices.Current.Settings.CurrentSettings;
        var sysDrive = AppServices.Current.Drives.GetDrive(
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            settings.CriticalSpaceThresholdPercent,
            settings.WarningSpaceThresholdPercent);

        if (sysDrive != null)
        {
            QuickStatusText.Text = $"{sysDrive.Name.TrimEnd('\\')} {sysDrive.FreePercentageDisplay}";
            StatusLed.Fill = sysDrive.AlertLevel switch
            {
                DriveAlertLevel.Critical => new SolidColorBrush(Colors.Red),
                DriveAlertLevel.Warning => new SolidColorBrush(Colors.Orange),
                _ => new SolidColorBrush(Colors.Green)
            };
        }
    }

    private void OnActiveOperationChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var op = AppServices.Current.ActiveOperation;
            if (op.IsActive)
            {
                ActivityBadgeText.Text = "Running";
                FlyoutOpName.Text = op.OperationName;
                FlyoutProgressBar.Value = Math.Max(5, op.ProgressPercentage);
                FlyoutPathText.Text = op.CurrentPath;
            }
            else
            {
                ActivityBadgeText.Text = "Idle";
                FlyoutOpName.Text = op.OperationName;
                FlyoutProgressBar.Value = 0;
                FlyoutPathText.Text = "Ready";
            }
        });
    }

    private void OnNavViewLoaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToPage(tag);
        }
    }

    private void NavigateToPage(string tag)
    {
        Type pageType = tag switch
        {
            "Overview" => typeof(OverviewPage),
            "StorageExplorer" => typeof(StorageExplorerPage),
            "DeepScan" => typeof(DeepScanPage),
            "LargeFiles" => typeof(LargeFilesPage),
            "CacheCleaner" => typeof(CacheCleanerPage),
            "ApplicationManager" => typeof(ApplicationManagerPage),
            "SystemCleanup" => typeof(SystemCleanupPage),
            "DuplicateFinder" => typeof(DuplicateFinderPage),
            "CleanupPlanner" => typeof(CleanupPlannerPage),
            "AiAssistant" => typeof(AiAssistantPage),
            "ActivityReports" => typeof(ActivityReportsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnGlobalSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = args.QueryText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return;

        string lower = query.ToLowerInvariant();
        if (lower.Contains("large") || lower.Contains("big"))
        {
            ContentFrame.Navigate(typeof(LargeFilesPage));
        }
        else if (lower.Contains("cache") || lower.Contains("temp") || lower.Contains("browser"))
        {
            ContentFrame.Navigate(typeof(CacheCleanerPage));
        }
        else if (lower.Contains("app") || lower.Contains("program") || lower.Contains("software"))
        {
            ContentFrame.Navigate(typeof(ApplicationManagerPage));
        }
        else if (lower.Contains("dism") || lower.Contains("system") || lower.Contains("update"))
        {
            ContentFrame.Navigate(typeof(SystemCleanupPage));
        }
        else if (lower.Contains("dup") || lower.Contains("clone"))
        {
            ContentFrame.Navigate(typeof(DuplicateFinderPage));
        }
        else if (lower.Contains("plan"))
        {
            ContentFrame.Navigate(typeof(CleanupPlannerPage));
        }
        else if (lower.Contains("report") || lower.Contains("export") || lower.Contains("history") || lower.Contains("quarantine"))
        {
            ContentFrame.Navigate(typeof(ActivityReportsPage));
        }
        else if (lower.Contains("setting") || lower.Contains("theme") || lower.Contains("ollama"))
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
        else
        {
            // Default to AI assistant to address natural language questions
            ContentFrame.Navigate(typeof(AiAssistantPage));
        }
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        if (this.XamlRoot?.Content is FrameworkElement root)
        {
            var nextTheme = root.RequestedTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = nextTheme;
            AppServices.Current.Settings.CurrentSettings.Theme = nextTheme.ToString();
            _ = AppServices.Current.Settings.SaveSettingsAsync(AppServices.Current.Settings.CurrentSettings);
        }
    }

    private void UpdateElevationStatus()
    {
        bool isAdmin = SystemMaintenanceService.IsRunningAsAdministrator();
        if (isAdmin)
        {
            ElevateIcon.Glyph = "\uE73E"; // Shield with checkmark
            ElevateIcon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 65)); // Emerald Green
            ElevateText.Text = "Administrator";
            ToolTipService.SetToolTip(ElevateButton, "CleanCore is running with full Administrator Privileges");
        }
        else
        {
            ElevateIcon.Glyph = "\uE7EF"; // Shield
            ElevateIcon.Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            ElevateText.Text = "Elevate to Admin";
            ToolTipService.SetToolTip(ElevateButton, "Restart CleanCore as Administrator to access protected system directories (PerfLogs, Config.Msi, MSOCache, etc.)");
        }
    }

    private async void OnElevateClick(object sender, RoutedEventArgs e)
    {
        bool isAdmin = SystemMaintenanceService.IsRunningAsAdministrator();
        if (isAdmin)
        {
            var dialog = new ContentDialog
            {
                Title = "Full Administrator Access",
                Content = "CleanCore is already running with full Administrator privileges and has full access to all local drives and system storage.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        else
        {
            RestartAsAdministrator();
        }
    }

    public static void RestartAsAdministrator()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (!string.IsNullOrEmpty(processPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(psi);
                Application.Current.Exit();
            }
        }
        catch
        {
            // User cancelled UAC prompt or elevation declined
        }
    }
}
