using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using CleanCore.Core.Reporting;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class ActivityReportsPage : Page
{
    public ObservableCollection<QuarantineRecord> QuarantineList { get; } = new();
    public ObservableCollection<CleanupJobResult> HistoryList { get; } = new();

    public ActivityReportsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        // 1. Lifetime space recovered
        long lifetime = await AppServices.Current.Database.GetTotalLifetimeSpaceRecoveredBytesAsync();
        LifetimeRecoveredText.Text = DriveModel.FormatBytes(lifetime);

        // 2. Quarantine size & records
        long quSize = AppServices.Current.Quarantine.GetTotalQuarantineSizeBytes();
        QuarantineSizeText.Text = DriveModel.FormatBytes(quSize);

        var quRecords = AppServices.Current.Quarantine.GetQuarantineRecords();
        QuarantineList.Clear();
        foreach (var r in quRecords.OrderByDescending(q => q.QuarantinedAt))
        {
            QuarantineList.Add(r);
        }
        QuarantineListView.ItemsSource = QuarantineList;
        NoQuarantineText.Visibility = QuarantineList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 3. History
        var history = await AppServices.Current.Database.GetCleanupHistoryAsync(50);
        HistoryList.Clear();
        foreach (var h in history)
        {
            HistoryList.Add(h);
        }
        HistoryListView.ItemsSource = HistoryList;
        JobsCountText.Text = $"{HistoryList.Count} Jobs";
    }

    private async void OnRestoreQuarantineClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is QuarantineRecord rec)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "Restore Quarantined File?",
                Content = $"Restore '{rec.FileName}' back to its original location?\n\nTarget path: {rec.OriginalPath}",
                PrimaryButtonText = "Restore File",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var (success, error) = await AppServices.Current.Quarantine.RestoreFileAsync(rec);
                if (success)
                {
                    await RefreshDataAsync();
                    ExportResultBar.Title = "File Restored";
                    ExportResultBar.Message = $"Successfully restored '{rec.FileName}' to '{rec.OriginalPath}'.";
                    ExportResultBar.Severity = InfoBarSeverity.Success;
                    ExportResultBar.IsOpen = true;
                }
                else
                {
                    var errDialog = new ContentDialog
                    {
                        Title = "Restoration Failed",
                        Content = error,
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                }
            }
        }
    }

    private async void OnPurgeQuarantineClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is QuarantineRecord rec)
        {
            if (AppServices.Current.Quarantine.PurgeQuarantineRecord(rec, out _))
            {
                await RefreshDataAsync();
            }
        }
    }

    private async void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        var data = BuildReportData();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string outputPath = Path.Combine(desktop, $"CleanCore_Report_{DateTime.Now:yyyyMMdd_HHmmss}.html");

        await ReportGenerator.ExportHtmlAsync(data, outputPath);

        ExportResultBar.Title = "HTML Report Exported";
        ExportResultBar.Message = $"Report saved to: {outputPath}";
        ExportResultBar.Severity = InfoBarSeverity.Success;
        ExportResultBar.IsOpen = true;

        try { Process.Start(new ProcessStartInfo { FileName = outputPath, UseShellExecute = true }); } catch { }
    }

    private async void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        var data = BuildReportData();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string outputPath = Path.Combine(desktop, $"CleanCore_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        await ReportGenerator.ExportCsvAsync(data, outputPath);

        ExportResultBar.Title = "CSV Report Exported";
        ExportResultBar.Message = $"Report saved to: {outputPath}";
        ExportResultBar.Severity = InfoBarSeverity.Success;
        ExportResultBar.IsOpen = true;
    }

    private async void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        var data = BuildReportData();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string outputPath = Path.Combine(desktop, $"CleanCore_Report_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        await ReportGenerator.ExportJsonAsync(data, outputPath);

        ExportResultBar.Title = "JSON Report Exported";
        ExportResultBar.Message = $"Report saved to: {outputPath}";
        ExportResultBar.Severity = InfoBarSeverity.Success;
        ExportResultBar.IsOpen = true;
    }

    private ReportData BuildReportData()
    {
        return new ReportData
        {
            Drives = AppServices.Current.Drives.GetDrives(),
            CompletedJob = HistoryList.FirstOrDefault(),
            TotalLifetimeRecoveredBytes = AppServices.Current.Database.GetTotalLifetimeSpaceRecoveredBytesAsync().GetAwaiter().GetResult()
        };
    }
}
