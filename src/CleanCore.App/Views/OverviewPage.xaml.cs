using System;
using System.Collections.ObjectModel;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class OverviewPage : Page
{
    public ObservableCollection<DriveModel> Drives { get; } = new();

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadDrives();
    }

    private void LoadDrives()
    {
        Drives.Clear();
        var settings = AppServices.Current.Settings.CurrentSettings;
        var driveList = AppServices.Current.Drives.GetDrives(
            settings.CriticalSpaceThresholdPercent,
            settings.WarningSpaceThresholdPercent);

        foreach (var d in driveList)
        {
            Drives.Add(d);
        }

        DrivesItemsControl.ItemsSource = Drives;

        // Check if system drive has critical alert
        if (AppServices.Current.Drives.HasCriticalSystemDrive(out var sysDrive, settings.CriticalSpaceThresholdPercent) && sysDrive != null)
        {
            CriticalAlertBanner.Title = $"Critical Low Space on System Drive ({sysDrive.Name.TrimEnd('\\')})";
            CriticalAlertBanner.Message = $"Only {sysDrive.FreeDisplay} ({sysDrive.FreePercentage:F1}%) remaining on the system partition. Run Deep Scan or Cache Cleaner to recover space.";
            CriticalAlertBanner.IsOpen = true;
        }
        else
        {
            CriticalAlertBanner.IsOpen = false;
        }
    }

    private void OnRefreshDrivesClick(object sender, RoutedEventArgs e)
    {
        LoadDrives();
    }

    private void OnExploreDriveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string drivePath)
        {
            Frame.Navigate(typeof(StorageExplorerPage), drivePath);
        }
    }

    private void OnDeepScanDriveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string drivePath)
        {
            Frame.Navigate(typeof(DeepScanPage), drivePath);
        }
    }

    private void OnQuickDeepScanClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DeepScanPage));
    }

    private void OnQuickCacheCleanerClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(CacheCleanerPage));
    }

    private void OnQuickLargeFilesClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(LargeFilesPage));
    }

    private void OnQuickAiAssistantClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(AiAssistantPage));
    }
}
