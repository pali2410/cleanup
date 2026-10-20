using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Ai;
using CleanCore.Core.Models;
using CleanCore_App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CleanCore_App.Views;

public sealed partial class AiAssistantPage : Page
{
    public ObservableCollection<AiChatMessage> Messages { get; } = new();
    private bool _isLlmAvailable;
    private string _currentModel = "llama3:latest";

    public AiAssistantPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadChatHistoryAsync();
        await TestLlmConnectionAsync();
    }

    private async Task LoadChatHistoryAsync()
    {
        Messages.Clear();
        var history = await AppServices.Current.Database.GetChatHistoryAsync();
        if (history.Count == 0)
        {
            // Initial greeting
            var greeting = new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "Welcome to CleanCore AI Assistant! I can analyze your local drives, discover large files, inspect browser & developer caches, explain storage usage patterns, and propose cleanup plans.\n\nType a question or select one of the suggested prompts below to get started."
            };
            Messages.Add(greeting);
            await AppServices.Current.Database.SaveChatMessageAsync(greeting);
        }
        else
        {
            foreach (var msg in history) Messages.Add(msg);
        }
        ChatListView.ItemsSource = Messages;
    }

    private async Task TestLlmConnectionAsync()
    {
        string baseUrl = AppServices.Current.Settings.CurrentSettings.OllamaBaseUrl;
        _isLlmAvailable = await AppServices.Current.Ollama.TestConnectionAsync(baseUrl, CancellationToken.None);

        if (_isLlmAvailable)
        {
            StatusDot.Fill = new SolidColorBrush(Colors.Green);
            StatusText.Text = "Connected to Ollama";

            var models = await AppServices.Current.Ollama.GetAvailableModelsAsync(baseUrl, CancellationToken.None);
            ModelSelector.Items.Clear();
            foreach (var m in models) ModelSelector.Items.Add(m);

            if (ModelSelector.Items.Count > 0)
            {
                ModelSelector.SelectedIndex = 0;
            }
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Colors.Orange);
            StatusText.Text = "Ollama Offline (Deterministic Tool Mode Active)";
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Testing...";
        await TestLlmConnectionAsync();
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector.SelectedItem is string m)
        {
            _currentModel = m;
        }
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string text)
        {
            PromptInputBox.Text = text;
            SendMessage();
        }
    }

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendMessage();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SendMessage();

    private async void SendMessage()
    {
        string prompt = PromptInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        PromptInputBox.Text = string.Empty;

        var userMsg = new AiChatMessage
        {
            Role = AiRole.User,
            Content = prompt
        };
        Messages.Add(userMsg);
        await AppServices.Current.Database.SaveChatMessageAsync(userMsg);

        SendButton.IsEnabled = false;

        // Process request through ToolRegistry or Local LLM
        _ = Task.Run(async () =>
        {
            string responseText;

            // Intent analysis & Tool dispatching
            string lower = prompt.ToLowerInvariant();
            if (lower.Contains("most space") || lower.Contains("drive") || lower.Contains("capacity"))
            {
                var outcome = await AppServices.Current.Tools.ExecuteToolAsync("GetDriveInventory", new(), null, CancellationToken.None);
                responseText = $"Here is the current capacity and space distribution for your connected drives:\n\n{outcome.OutputJson}";
            }
            else if (lower.Contains("large file") || lower.Contains("larger than"))
            {
                long minBytes = lower.Contains("1 gb") ? 1073741824 : (lower.Contains("2 gb") ? 2147483648 : 524288000);
                var args = new Dictionary<string, object?> { ["minSizeBytes"] = minBytes };
                var outcome = await AppServices.Current.Tools.ExecuteToolAsync("SearchLargeFiles", args, null, CancellationToken.None);
                responseText = $"Discovered large files exceeding threshold:\n\n{outcome.OutputJson}";
            }
            else if (lower.Contains("cache") || lower.Contains("browser") || lower.Contains("developer"))
            {
                var outcome = await AppServices.Current.Tools.ExecuteToolAsync("AnalyzeCacheProviders", new(), null, CancellationToken.None);
                responseText = $"Analyzed application and system cache providers:\n\n{outcome.OutputJson}";
            }
            else if (lower.Contains("plan") || lower.Contains("cleanup"))
            {
                var outcome = await AppServices.Current.Tools.ExecuteToolAsync("GenerateCleanupPlan", new(), null, CancellationToken.None);
                responseText = $"Generated cleanup proposal plan for your review:\n\n{outcome.OutputJson}";
            }
            else if (_isLlmAvailable)
            {
                // Call local Ollama
                try
                {
                    string systemPrompt = "You are CleanCore AI Assistant, a Windows storage intelligence expert. Provide concise, accurate storage analysis and cleanup recommendations. Do not suggest arbitrary shell deletion commands.";
                    responseText = await AppServices.Current.Ollama.GenerateChatResponseAsync(
                        AppServices.Current.Settings.CurrentSettings.OllamaBaseUrl,
                        _currentModel,
                        Messages.ToList(),
                        systemPrompt,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    responseText = $"Ollama query error: {ex.Message}";
                }
            }
            else
            {
                responseText = "I can analyze your drives, discover large files, inspect application caches, and generate a safe cleanup plan. Try asking:\n- 'What is consuming the most space on C:?'\n- 'Show me files larger than 1 GB'\n- 'Analyze my browser and developer caches'\n- 'Prepare a cleanup plan'";
            }

            var assistantMsg = new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = responseText
            };

            await AppServices.Current.Database.SaveChatMessageAsync(assistantMsg);

            DispatcherQueue.TryEnqueue(() =>
            {
                Messages.Add(assistantMsg);
                ChatListView.ScrollIntoView(assistantMsg);
                SendButton.IsEnabled = true;
            });
        });
    }
}
