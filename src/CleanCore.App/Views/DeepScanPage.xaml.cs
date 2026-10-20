using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Classification;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CleanCore_App.Views;

public class CategorySummaryItem
{
    public FileCategory Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FileCount { get; set; }
    public double Percentage { get; set; }
    public string SizeDisplay => DriveModel.FormatBytes(TotalBytes);
    public string PercentageDisplay => $"{Percentage:F1}% of measured disk footprint";
    public string CountDisplay => $"{FileCount:N0} items";
}

public sealed partial class DeepScanPage : Page
{
    private CancellationTokenSource? _scanCts;
    private string _targetDrive = "C:\\";
    public ObservableCollection<CategorySummaryItem> Categories { get; } = new();

    public DeepScanPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            _targetDrive = path;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateDrives();
    }

    private void PopulateDrives()
    {
        TargetDriveComboBox.Items.Clear();
        var drives = AppServices.Current.Drives.GetDrives();
        int idx = 0;
        int i = 0;

        foreach (var d in drives)
        {
            var item = new ComboBoxItem { Content = $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel}) - {d.TotalDisplay}", Tag = d.Name };
            TargetDriveComboBox.Items.Add(item);
            if (string.Equals(Path.GetPathRoot(_targetDrive), d.Name, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
            }
            i++;
        }

        if (TargetDriveComboBox.Items.Count > 0)
        {
            TargetDriveComboBox.SelectedIndex = idx;
        }
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (TargetDriveComboBox.SelectedItem is ComboBoxItem item && item.Tag is string drive)
        {
            _targetDrive = drive;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        ProgressCard.Visibility = Visibility.Visible;
        AnalyzeButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;

        var progress = new Progress<ScanProgressReport>(r =>
        {
            PhaseText.Text = r.CurrentPhase;
            SpeedText.Text = r.SpeedDisplay;
            PathText.Text = $"Scanning: {r.CurrentDirectory}";
            ScannedMetricsText.Text = $"{r.FilesDiscovered:N0} files | {r.BytesDisplay}";
            AppServices.Current.ActiveOperation.Update("Deep Scan", r.CurrentDirectory, r.FilesDiscovered, r.BytesScanned, r.EstimatedPercent ?? 50.0);
        });

        Task.Run(async () =>
        {
            try
            {
                int concurrency = AppServices.Current.Settings.CurrentSettings.ScanConcurrency;
                var root = await AppServices.Current.Scanner.ScanDirectoryAsync(_targetDrive, deepScan: true, concurrency, progress, ct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    AppServices.Current.LastScannedRoot = root;
                    ProcessScanResults(root);

                    ProgressCard.Visibility = Visibility.Collapsed;
                    AnalyzeButton.IsEnabled = true;
                    CancelButton.Visibility = Visibility.Collapsed;
                    AppServices.Current.ActiveOperation.Complete("Deep Scan Completed");
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ProgressCard.Visibility = Visibility.Collapsed;
                    AnalyzeButton.IsEnabled = true;
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PhaseText.Text = $"Error: {ex.Message}";
                    AnalyzeButton.IsEnabled = true;
                    CancelButton.Visibility = Visibility.Collapsed;
                });
            }
        }, ct);
    }

    private void ProcessScanResults(StorageNode root)
    {
        var categoryMap = new Dictionary<FileCategory, (long bytes, long count)>();
        long reclaimableBytes = 0;
        long totalMeasured = 0;
        long totalFiles = 0;

        void Aggregate(StorageNode node)
        {
            if (!node.IsDirectory)
            {
                totalMeasured += node.SizeBytes;
                totalFiles++;

                if (!categoryMap.TryGetValue(node.Category, out var current))
                {
                    current = (0, 0);
                }
                categoryMap[node.Category] = (current.bytes + node.SizeBytes, current.count + 1);

                if (node.Category == FileCategory.TemporaryFiles || node.Category == FileCategory.ApplicationCaches)
                {
                    reclaimableBytes += node.SizeBytes;
                }
            }

            foreach (var child in node.Children)
            {
                Aggregate(child);
            }
        }

        Aggregate(root);

        TotalMeasuredText.Text = DriveModel.FormatBytes(totalMeasured);
        FilesCountText.Text = $"{totalFiles:N0} files indexed";
        PotentialReclaimableText.Text = DriveModel.FormatBytes(reclaimableBytes);

        Categories.Clear();
        foreach (var kvp in categoryMap.OrderByDescending(c => c.Value.bytes))
        {
            double percent = totalMeasured > 0 ? (double)kvp.Value.bytes / totalMeasured * 100.0 : 0.0;
            Categories.Add(new CategorySummaryItem
            {
                Category = kvp.Key,
                Name = kvp.Key.GetDisplayName(),
                Explanation = GetCategoryExplanation(kvp.Key),
                TotalBytes = kvp.Value.bytes,
                FileCount = kvp.Value.count,
                Percentage = percent
            });
        }

        CategoryItemsControl.ItemsSource = Categories;
    }

    private static string GetCategoryExplanation(FileCategory cat) => cat switch
    {
        FileCategory.OperatingSystem => "Windows kernel, core servicing libraries, system assemblies, and drivers.",
        FileCategory.InstalledApps => "Program Files application installations and associated binaries.",
        FileCategory.UserDocuments => "User documents, text, worksheets, presentations, and data tables.",
        FileCategory.Downloads => "Files downloaded through web browsers and utilities.",
        FileCategory.Images => "Photos, icons, vector art, and graphics assets.",
        FileCategory.Videos => "Video files, movies, and screen captures.",
        FileCategory.Audio => "Music, voice recordings, and sound effects.",
        FileCategory.Archives => "Zip, 7z, tar, iso, and compressed archives.",
        FileCategory.Development => "Source code repositories and programming scripts.",
        FileCategory.BuildOutputs => "Compiled build directories (bin, obj, node_modules).",
        FileCategory.ApplicationCaches => "Cached application data and browser assets.",
        FileCategory.TemporaryFiles => "Scratch temp files, installer caches, and crash dumps.",
        FileCategory.Logs => "Diagnostic logs, Event Trace logs, and telemetry records.",
        FileCategory.VirtualMachines => "Virtual hard disks (VHD/VHDX/VMDK) and container layers.",
        FileCategory.Backups => "System restore images and backup archives.",
        _ => "Unclassified or miscellaneous files."
    };

    private void OnCancelClick(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void OnPreparePlanClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(CleanupPlannerPage));
    }
}
