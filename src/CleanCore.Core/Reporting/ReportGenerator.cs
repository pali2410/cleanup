using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CleanCore.Core.Models;

namespace CleanCore.Core.Reporting;

public class ReportData
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public List<DriveModel> Drives { get; set; } = new();
    public List<LargeFileEntry> LargeFiles { get; set; } = new();
    public List<CacheProviderResult> CacheResults { get; set; } = new();
    public List<DuplicateGroup> DuplicateGroups { get; set; } = new();
    public CleanupPlan? ProposedPlan { get; set; }
    public CleanupJobResult? CompletedJob { get; set; }
    public long TotalLifetimeRecoveredBytes { get; set; }
}

public static class ReportGenerator
{
    public static async Task<string> ExportJsonAsync(ReportData data, string outputPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(data, options);
        await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8);
        return outputPath;
    }

    public static async Task<string> ExportCsvAsync(ReportData data, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Report Type,Name,Path/Detail,Size (Bytes),Formatted Size,Category/Status,Additional Info");

        foreach (var d in data.Drives)
        {
            sb.AppendLine($"Drive,{Escape(d.VolumeLabel)},{Escape(d.Name)},{d.TotalBytes},{d.TotalDisplay},{d.DriveType},{d.FreePercentageDisplay}");
        }

        foreach (var f in data.LargeFiles)
        {
            sb.AppendLine($"Large File,{Escape(f.FileName)},{Escape(f.FullPath)},{f.SizeBytes},{f.SizeDisplay},{f.CategoryDisplay},{f.ModifiedDisplay}");
        }

        foreach (var c in data.CacheResults)
        {
            sb.AppendLine($"Cache Target,{Escape(c.DisplayName)},{Escape(c.Category)},{c.TotalSizeBytes},{c.SizeDisplay},{c.RiskBadge},{c.TotalItemCount} items");
        }

        foreach (var g in data.DuplicateGroups)
        {
            foreach (var item in g.Items)
            {
                sb.AppendLine($"Duplicate Copy,{Escape(item.FileName)},{Escape(item.FullPath)},{item.SizeBytes},{item.SizeDisplay},SHA256:{g.Sha256Hash.Substring(0, 8)},Original Preserved: {item.IsOriginalPreserved}");
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }

    public static async Task<string> ExportHtmlAsync(ReportData data, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine("  <title>CleanCore Storage Intelligence Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 2rem; }");
        sb.AppendLine("    .container { max-width: 1200px; margin: 0 auto; }");
        sb.AppendLine("    h1, h2, h3 { color: #38bdf8; font-weight: 600; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 0.25rem 0.6rem; border-radius: 9999px; font-size: 0.75rem; font-weight: 600; }");
        sb.AppendLine("    .badge-safe { background: #065f46; color: #34d399; }");
        sb.AppendLine("    .badge-warn { background: #854d0e; color: #facc15; }");
        sb.AppendLine("    .card-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 1rem; margin-bottom: 2rem; }");
        sb.AppendLine("    .card { background: #1e293b; border: 1px solid #334155; border-radius: 0.75rem; padding: 1.25rem; }");
        sb.AppendLine("    .card h3 { margin-top: 0; margin-bottom: 0.5rem; }");
        sb.AppendLine("    .metric { font-size: 1.8rem; font-weight: 700; color: #f1f5f9; margin-bottom: 0.5rem; }");
        sb.AppendLine("    .progress-bar { background: #334155; border-radius: 9999px; height: 8px; overflow: hidden; margin: 0.5rem 0; }");
        sb.AppendLine("    .progress-fill { background: #38bdf8; height: 100%; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 1rem; background: #1e293b; border-radius: 0.5rem; overflow: hidden; }");
        sb.AppendLine("    th, td { padding: 0.75rem 1rem; text-align: left; border-bottom: 1px solid #334155; }");
        sb.AppendLine("    th { background: #0f172a; color: #94a3b8; font-size: 0.85rem; text-transform: uppercase; }");
        sb.AppendLine("    tr:hover { background: #243248; }");
        sb.AppendLine("    .footer { text-align: center; margin-top: 3rem; color: #64748b; font-size: 0.85rem; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <h1>CleanCore — Storage Intelligence Report</h1>");
        sb.AppendLine($"    <p style=\"color: #94a3b8;\">Generated: {data.GeneratedAt:yyyy-MM-dd HH:mm:ss} | Local Storage Diagnostics</p>");

        // Drives Section
        sb.AppendLine("    <h2>Connected Storage Volumes</h2>");
        sb.AppendLine("    <div class=\"card-grid\">");
        foreach (var d in data.Drives)
        {
            sb.AppendLine("      <div class=\"card\">");
            sb.AppendLine($"        <h3>{d.DisplayTitle}</h3>");
            sb.AppendLine($"        <div class=\"metric\">{d.FreeDisplay} <span style=\"font-size: 1rem; font-weight: normal; color: #94a3b8;\">Free</span></div>");
            sb.AppendLine("        <div class=\"progress-bar\">");
            sb.AppendLine($"          <div class=\"progress-fill\" style=\"width: {d.UsedPercentage:F1}%;\"></div>");
            sb.AppendLine("        </div>");
            sb.AppendLine($"        <p style=\"font-size: 0.85rem; color: #94a3b8; margin: 0;\">Total: {d.TotalDisplay} | {d.FileSystem} | {d.HealthStatus}</p>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");

        // Cache Providers Section
        if (data.CacheResults.Count > 0)
        {
            sb.AppendLine("    <h2>Cache & Temporary Cleanup Candidates</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <thead><tr><th>Provider</th><th>Category</th><th>Risk Level</th><th>Total Reclaimable</th><th>Items</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var c in data.CacheResults)
            {
                string badgeClass = c.RiskLevel == CleanupRiskLevel.Safe ? "badge-safe" : "badge-warn";
                sb.AppendLine($"        <tr><td><strong>{c.DisplayName}</strong></td><td>{c.Category}</td><td><span class=\"badge {badgeClass}\">{c.RiskBadge}</span></td><td>{c.SizeDisplay}</td><td>{c.TotalItemCount}</td></tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
        }

        // Large Files Section
        if (data.LargeFiles.Count > 0)
        {
            sb.AppendLine("    <h2>Top Large Files Discovered</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <thead><tr><th>File Name</th><th>Category</th><th>Size</th><th>Path</th><th>Modified</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var f in data.LargeFiles.Take(15))
            {
                sb.AppendLine($"        <tr><td><strong>{f.FileName}</strong></td><td>{f.CategoryDisplay}</td><td>{f.SizeDisplay}</td><td style=\"word-break: break-all;\">{f.FullPath}</td><td>{f.ModifiedDisplay}</td></tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
        }

        sb.AppendLine("    <div class=\"footer\">");
        sb.AppendLine("      <p>CleanCore v1.0 Production Release &bull; Native Windows Storage Intelligence Platform &bull; No Cloud Dependencies</p>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        return $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
