using System;
using System.Collections.Generic;

namespace CleanCore.Core.Models;

public enum AiRole
{
    User,
    Assistant,
    System,
    Tool
}

public class AiChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AiRole Role { get; set; } = AiRole.User;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<AiToolExecutionCard>? ToolCalls { get; set; }
    public bool IsThinking { get; set; }

    public string RoleDisplay => Role switch
    {
        AiRole.User => "You",
        AiRole.Assistant => "CleanCore AI",
        AiRole.System => "System",
        AiRole.Tool => "Tool Action",
        _ => "AI"
    };

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");
    public bool IsUserMessage => Role == AiRole.User;
}

public class AiToolExecutionCard
{
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsSummary { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending, AwaitingApproval, Executing, Completed, Rejected, Failed
    public bool RequiresApproval { get; set; }
    public string? ResultSummary { get; set; }
    public string? ErrorMessage { get; set; }
    public string ApprovalId { get; set; } = Guid.NewGuid().ToString("N");
}

public enum AiAutonomyLevel
{
    StrictApproval,    // All actions and proposals require manual user review & approval
    SemiAutonomous,    // Read-only tools execute automatically, destructive tools require approval
    UnattendedReadOnly // Read-only tools execute automatically, destructive tools blocked
}

public class LlmEndpointConfig
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string SelectedModel { get; set; } = "llama3:latest";
    public List<string> AvailableModels { get; set; } = new();
    public bool IsConnected { get; set; }
    public string StatusMessage { get; set; } = "Not connected";
    public AiAutonomyLevel AutonomyLevel { get; set; } = AiAutonomyLevel.SemiAutonomous;
}
