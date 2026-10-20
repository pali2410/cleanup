using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CleanCore.Core.Models;
using Microsoft.Data.Sqlite;

namespace CleanCore.Core.Data;

public interface ICleanCoreDatabase
{
    Task InitializeAsync();
    Task SaveSettingAsync(string key, string value);
    Task<string?> GetSettingAsync(string key, string? defaultValue = null);
    Task RecordCleanupJobAsync(CleanupJobResult job);
    Task<List<CleanupJobResult>> GetCleanupHistoryAsync(int limit = 50);
    Task SaveChatMessageAsync(AiChatMessage message);
    Task<List<AiChatMessage>> GetChatHistoryAsync(int limit = 100);
    Task ClearScanHistoryAsync();
    Task ClearChatHistoryAsync();
    Task<long> GetTotalLifetimeSpaceRecoveredBytesAsync();
}

public class CleanCoreDatabase : ICleanCoreDatabase
{
    private readonly string _connectionString;

    public CleanCoreDatabase()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbDir = Path.Combine(localAppData, "CleanCore");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "cleancore.db");
        _connectionString = $"Data Source={dbPath};";
    }

    public async Task InitializeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Enable Write-Ahead Logging for high concurrency
        using var walCmd = connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL;";
        await walCmd.ExecuteNonQueryAsync();

        string sql = @"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS CleanupJobs (
                JobId TEXT PRIMARY KEY,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NOT NULL,
                TotalTargets INTEGER NOT NULL,
                SuccessCount INTEGER NOT NULL,
                FailureCount INTEGER NOT NULL,
                SkippedLockedCount INTEGER NOT NULL,
                TotalBytesRecovered INTEGER NOT NULL,
                Status TEXT NOT NULL,
                DetailsJson TEXT
            );

            CREATE TABLE IF NOT EXISTS ChatHistory (
                Id TEXT PRIMARY KEY,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ToolCallsJson TEXT
            );

            CREATE INDEX IF NOT EXISTS IDX_CleanupJobs_CompletedAt ON CleanupJobs(CompletedAt DESC);
            CREATE INDEX IF NOT EXISTS IDX_ChatHistory_Timestamp ON ChatHistory(Timestamp ASC);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveSettingAsync(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Settings (Key, Value, UpdatedAt)
            VALUES (@key, @value, @updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
        ";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.Now.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetSettingAsync(string key, string? defaultValue = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key LIMIT 1;";
        cmd.Parameters.AddWithValue("@key", key);

        var val = await cmd.ExecuteScalarAsync();
        return val != null ? Convert.ToString(val) : defaultValue;
    }

    public async Task RecordCleanupJobAsync(CleanupJobResult job)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO CleanupJobs (
                JobId, StartedAt, CompletedAt, TotalTargets, SuccessCount, FailureCount,
                SkippedLockedCount, TotalBytesRecovered, Status, DetailsJson
            ) VALUES (
                @jobId, @startedAt, @completedAt, @totalTargets, @successCount, @failureCount,
                @skippedLockedCount, @totalBytesRecovered, @status, @detailsJson
            );
        ";

        cmd.Parameters.AddWithValue("@jobId", job.JobId);
        cmd.Parameters.AddWithValue("@startedAt", job.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@completedAt", job.CompletedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@totalTargets", job.TotalTargets);
        cmd.Parameters.AddWithValue("@successCount", job.SuccessCount);
        cmd.Parameters.AddWithValue("@failureCount", job.FailureCount);
        cmd.Parameters.AddWithValue("@skippedLockedCount", job.SkippedLockedCount);
        cmd.Parameters.AddWithValue("@totalBytesRecovered", job.TotalBytesRecovered);
        cmd.Parameters.AddWithValue("@status", job.Status);
        cmd.Parameters.AddWithValue("@detailsJson", JsonSerializer.Serialize(job.Results));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CleanupJobResult>> GetCleanupHistoryAsync(int limit = 50)
    {
        var list = new List<CleanupJobResult>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT JobId, StartedAt, CompletedAt, TotalTargets, SuccessCount, FailureCount,
                   SkippedLockedCount, TotalBytesRecovered, Status, DetailsJson
            FROM CleanupJobs
            ORDER BY CompletedAt DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var job = new CleanupJobResult
            {
                JobId = reader.GetString(0),
                StartedAt = DateTime.Parse(reader.GetString(1)),
                CompletedAt = DateTime.Parse(reader.GetString(2)),
                TotalTargets = reader.GetInt32(3),
                SuccessCount = reader.GetInt32(4),
                FailureCount = reader.GetInt32(5),
                SkippedLockedCount = reader.GetInt32(6),
                TotalBytesRecovered = reader.GetInt64(7),
                Status = reader.GetString(8)
            };

            string? details = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (!string.IsNullOrEmpty(details))
            {
                try
                {
                    job.Results = JsonSerializer.Deserialize<List<CleanupTargetResult>>(details) ?? new();
                }
                catch { }
            }

            list.Add(job);
        }

        return list;
    }

    public async Task SaveChatMessageAsync(AiChatMessage message)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ChatHistory (Id, Role, Content, Timestamp, ToolCallsJson)
            VALUES (@id, @role, @content, @timestamp, @toolCallsJson);
        ";
        cmd.Parameters.AddWithValue("@id", message.Id);
        cmd.Parameters.AddWithValue("@role", message.Role.ToString());
        cmd.Parameters.AddWithValue("@content", message.Content);
        cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@toolCallsJson", message.ToolCalls != null ? JsonSerializer.Serialize(message.ToolCalls) : (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<AiChatMessage>> GetChatHistoryAsync(int limit = 100)
    {
        var list = new List<AiChatMessage>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Role, Content, Timestamp, ToolCallsJson FROM ChatHistory ORDER BY Timestamp ASC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var msg = new AiChatMessage
            {
                Id = reader.GetString(0),
                Role = Enum.TryParse<AiRole>(reader.GetString(1), out var r) ? r : AiRole.User,
                Content = reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3))
            };

            if (!reader.IsDBNull(4))
            {
                try
                {
                    msg.ToolCalls = JsonSerializer.Deserialize<List<AiToolExecutionCard>>(reader.GetString(4));
                }
                catch { }
            }

            list.Add(msg);
        }

        return list;
    }

    public async Task ClearScanHistoryAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CleanupJobs;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearChatHistoryAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ChatHistory;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> GetTotalLifetimeSpaceRecoveredBytesAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(TotalBytesRecovered), 0) FROM CleanupJobs;";
        var val = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(val);
    }
}
