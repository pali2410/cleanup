using System;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanCore_App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current.Settings.CurrentSettings;
        if (s.Theme == "Light") LightThemeRadio.IsChecked = true;
        else DarkThemeRadio.IsChecked = true;

        ConcurrencySlider.Value = s.ScanConcurrency;
        ConcurrencyValueText.Text = $"{s.ScanConcurrency} Threads";

        WarningThresholdBox.Value = s.WarningSpaceThresholdPercent;
        CriticalThresholdBox.Value = s.CriticalSpaceThresholdPercent;

        OllamaEndpointBox.Text = s.OllamaBaseUrl;
        OllamaModelBox.Text = s.SelectedModel;

        AutonomyComboBox.SelectedIndex = s.AutonomyLevel switch
        {
            AiAutonomyLevel.StrictApproval => 0,
            AiAutonomyLevel.SemiAutonomous => 1,
            AiAutonomyLevel.UnattendedReadOnly => 2,
            _ => 1
        };
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string theme)
        {
            if (this.XamlRoot?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme == "Light" ? ElementTheme.Light : ElementTheme.Dark;
            }
        }
    }

    private void OnConcurrencyChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ConcurrencyValueText != null)
        {
            ConcurrencyValueText.Text = $"{(int)e.NewValue} Threads";
        }
    }

    private void OnThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // Value adjusted
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current.Settings.CurrentSettings;
        s.Theme = LightThemeRadio.IsChecked == true ? "Light" : "Dark";
        s.ScanConcurrency = (int)ConcurrencySlider.Value;
        s.WarningSpaceThresholdPercent = WarningThresholdBox.Value;
        s.CriticalSpaceThresholdPercent = CriticalThresholdBox.Value;
        s.OllamaBaseUrl = OllamaEndpointBox.Text.Trim();
        s.SelectedModel = OllamaModelBox.Text.Trim();
        s.AutonomyLevel = AutonomyComboBox.SelectedIndex switch
        {
            0 => AiAutonomyLevel.StrictApproval,
            1 => AiAutonomyLevel.SemiAutonomous,
            2 => AiAutonomyLevel.UnattendedReadOnly,
            _ => AiAutonomyLevel.SemiAutonomous
        };

        await AppServices.Current.Settings.SaveSettingsAsync(s);

        SettingsSavedBar.IsOpen = true;
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.Database.ClearScanHistoryAsync();
        var dialog = new ContentDialog
        {
            Title = "History Cleared",
            Content = "All local cleanup audit records have been cleared from the SQLite database.",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void OnClearChatClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.Database.ClearChatHistoryAsync();
        var dialog = new ContentDialog
        {
            Title = "Chat Cleared",
            Content = "All stored AI Assistant conversation history has been cleared.",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
