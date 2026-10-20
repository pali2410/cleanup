using System;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.SystemMaintenance;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class SystemCleanupPage : Page
{
    public SystemCleanupPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TasksItemsControl.ItemsSource = AppServices.Current.SystemMaintenance.AvailableTasks;
    }

    private async void OnRunTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is SystemMaintenanceTask task)
        {
            var confirmDialog = new ContentDialog
            {
                Title = $"Run {task.Title}?",
                Content = $"{task.Description}\n\n{(task.RequiresElevation ? "This task requires administrator elevation and will prompt with Windows UAC.\n\n" : "")}This process may take several minutes to analyze and service system components. Do not restart during maintenance.",
                PrimaryButtonText = "Execute Maintenance",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            b.IsEnabled = false;
            ExecutionOutcomeBar.IsOpen = true;
            ExecutionOutcomeBar.Severity = InfoBarSeverity.Informational;
            ExecutionOutcomeBar.Title = $"Executing {task.Title}...";
            ExecutionOutcomeBar.Message = "Running official servicing utility. Please wait...";

            var result = await AppServices.Current.SystemMaintenance.RunTaskAsync(task, CancellationToken.None);

            b.IsEnabled = true;
            ExecutionOutcomeBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ExecutionOutcomeBar.Title = result.Success ? $"{task.Title} Completed" : $"{task.Title} Failed";
            ExecutionOutcomeBar.Message = result.ErrorMessage;
        }
    }
}
