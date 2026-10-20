using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanCore.Core.Models;

namespace CleanCore.Core.Ai;

public interface IOllamaClient
{
    Task<bool> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken);
    Task<List<string>> GetAvailableModelsAsync(string baseUrl, CancellationToken cancellationToken);
    Task<string> GenerateChatResponseAsync(
        string baseUrl,
        string model,
        List<AiChatMessage> history,
        string systemPrompt,
        CancellationToken cancellationToken);
}

public class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;

    public OllamaClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/tags";
            var resp = await _httpClient.GetAsync(url, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetAvailableModelsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var models = new List<string>();
        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/tags";
            var resp = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>(url, cancellationToken);
            if (resp?.models != null)
            {
                foreach (var m in resp.models)
                {
                    if (!string.IsNullOrWhiteSpace(m.name))
                    {
                        models.Add(m.name);
                    }
                }
            }
        }
        catch { }

        return models;
    }

    public async Task<string> GenerateChatResponseAsync(
        string baseUrl,
        string model,
        List<AiChatMessage> history,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var msg in history)
        {
            messages.Add(new
            {
                role = msg.Role == AiRole.Assistant ? "assistant" : "user",
                content = msg.Content
            });
        }

        var payload = new
        {
            model = model,
            messages = messages,
            stream = false
        };

        var url = baseUrl.TrimEnd('/') + "/api/chat";
        var resp = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: cancellationToken);
        return json?.message?.content ?? "No response received from local LLM.";
    }

    private class OllamaTagsResponse
    {
        public List<OllamaModelItem>? models { get; set; }
    }

    private class OllamaModelItem
    {
        public string? name { get; set; }
    }

    private class OllamaChatResponse
    {
        public OllamaMessage? message { get; set; }
    }

    private class OllamaMessage
    {
        public string? role { get; set; }
        public string? content { get; set; }
    }
}
