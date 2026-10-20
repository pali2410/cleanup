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

public sealed partial class CleanupPlannerPage : Page
{
    private CleanupPlan? _currentPlan;
    public ObservableCollection<CleanupCandidate> CandidatesList { get; } = new();

    public CleanupPlannerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        QuarantineToggleCheckBox.IsChecked = AppServices.Current.Settings.CurrentSettings.EnableQuarantineByDefault;
        GeneratePlan();
    }

    private void GeneratePlan()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        CandidatesListView.Visibility = Visibility.Collapsed;
        ExecutePlanButton.IsEnabled = false;
        LoadingText.Text = "Generating cleanup plan...";
        LoadingItemCountText.Text = "Inspecting cache and temporary locations...";
        LoadingSubText.Text = "Analyzing providers...";
        ExecutionProgressBar.IsIndeterminate = true;

        Task.Run(async () =>
        {
            var candidates = new List<CleanupCandidate>();

            // Collect candidates from Cache providers
            try
            {
                var cacheResults = await AppServices.Current.Cache.AnalyzeAllAsync(null, CancellationToken.None);
                foreach (var provider in cacheResults)
                {
                    foreach (var target in provider.Targets)
                    {
                        candidates.Add(new CleanupCandidate
                        {
                            Name = target.Name,
                            Path = target.Path,
                            SizeBytes = target.SizeBytes,
                            Category = provider.Category,
                            RiskLevel = provider.RiskLevel,
                            Reason = target.SideEffectDescription,
                            RequiresElevation = provider.RequiresElevation,
                            ExpectedSideEffects = target.SideEffectDescription,
                            IsReversible = true,
                            IsSelected = provider.RiskLevel == CleanupRiskLevel.Safe && !target.IsLocked
                        });
                    }
                }
            }
            catch { }

            var plan = AppServices.Current.Cleanup.CreatePlanFromCandidates(candidates);
            _currentPlan = plan;
            AppServices.Current.CurrentCleanupPlan = plan;

            DispatcherQueue.TryEnqueue(() =>
            {
                CandidatesList.Clear();
                foreach (var c in plan.Candidates)
                {
                    CandidatesList.Add(c);
                }

                CandidatesListView.ItemsSource = CandidatesList;
                UpdateDashboardMetrics();

                LoadingPanel.Visibility = Visibility.Collapsed;
                CandidatesListView.Visibility = Visibility.Visible;
                ExecutePlanButton.IsEnabled = true;
            });
        });
    }

    private void UpdateDashboardMetrics()
    {
        if (_currentPlan == null) return;

        TotalSelectedMetricText.Text = _currentPlan.TotalSelectedDisplay;
        CandidatesCountText.Text = $"{_currentPlan.SelectedCount} of {_currentPlan.CandidateCount} items selected";
        SafeSpaceMetricText.Text = _currentPlan.SafeSelectedDisplay;
        ReviewSpaceMetricText.Text = _currentPlan.ReviewRequiredDisplay;
        ExcludedSpaceMetricText.Text = _currentPlan.ExcludedDisplay;
    }

    private void OnCandidateCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateDashboardMetrics();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null) return;
        foreach (var c in CandidatesList)
        {
            if (!c.IsLocked)
            {
                c.IsSelected = true;
            }
        }
        UpdateDashboardMetrics();
    }

    private void OnSelectSafeOnlyClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null) return;
        foreach (var c in CandidatesList)
        {
            c.IsSelected = c.RiskLevel == CleanupRiskLevel.Safe && !c.IsLocked;
        }
        UpdateDashboardMetrics();
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null) return;
        foreach (var c in CandidatesList)
        {
            c.IsSelected = false;
        }
        UpdateDashboardMetrics();
    }

    private void OnRegeneratePlanClick(object sender, RoutedEventArgs e) => GeneratePlan();

    private async void OnExecutePlanClick(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.SelectedCount == 0)
        {
            var noDialog = new ContentDialog
            {
                Title = "No Candidates Selected",
                Content = "Please select at least one cleanup candidate item before executing the plan.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await noDialog.ShowAsync();
            return;
        }

        var confirmDialog = new ContentDialog
        {
            Title = "Execute Cleanup Plan?",
            Content = $"You are about to execute cleanup for {_currentPlan.SelectedCount} items totaling {_currentPlan.TotalSelectedDisplay}.\n\nQuarantine Protection: {(QuarantineToggleCheckBox.IsChecked == true ? "Enabled (Files will be safely backed up to quarantine)" : "Disabled (Immediate permanent delete)")}\n\nProceed with cleanup?",
            PrimaryButtonText = "Execute Plan",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

        ExecutePlanButton.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Executing approved cleanup...";
        LoadingItemCountText.Text = $"Processing 0 of {_currentPlan.SelectedCount} items";
        LoadingSubText.Text = "Starting cleanup engine...";
        ExecutionProgressBar.IsIndeterminate = false;
        ExecutionProgressBar.Value = 0;

        var progress = new Progress<ScanProgressReport>(r =>
        {
            ExecutionProgressBar.Value = r.EstimatedPercent ?? 0;
            LoadingItemCountText.Text = $"Processing {r.FilesDiscovered} of {_currentPlan.SelectedCount} ({DriveModel.FormatBytes(r.BytesScanned)} recovered)";
            LoadingSubText.Text = r.CurrentDirectory;
            AppServices.Current.ActiveOperation.Update("Executing Plan", r.CurrentDirectory, r.FilesDiscovered, r.BytesScanned, r.EstimatedPercent ?? 50.0);
        });

        bool quarantine = QuarantineToggleCheckBox.IsChecked == true;
        var jobResult = await AppServices.Current.Cleanup.ExecutePlanAsync(_currentPlan, quarantine, progress, CancellationToken.None);

        // Record in SQLite
        await AppServices.Current.Database.RecordCleanupJobAsync(jobResult);

        LoadingPanel.Visibility = Visibility.Collapsed;
        ExecutePlanButton.IsEnabled = true;

        ExecutionOutcomeBar.Title = "Plan Execution Completed";
        ExecutionOutcomeBar.Message = $"Recovered {jobResult.BytesRecoveredDisplay}. Succeeded: {jobResult.SuccessCount}, Failed: {jobResult.FailureCount}, Skipped: {jobResult.SkippedLockedCount}.";
        ExecutionOutcomeBar.Severity = InfoBarSeverity.Success;
        ExecutionOutcomeBar.IsOpen = true;

        // Re-generate fresh plan
        GeneratePlan();
    }
}
