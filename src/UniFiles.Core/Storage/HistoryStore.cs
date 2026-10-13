using Microsoft.Data.Sqlite;
using UniFiles.Core.Jobs;

namespace UniFiles.Core.Storage;

public sealed record HistoryEntry(
    long Id,
    DateTime FinishedAt,
    string ToolId,
    int FileCount,
    int FailedCount,
    long InputBytes,
    long OutputBytes,
    JobStatus Status,
    string OutputDirectory);

/// <summary>Past jobs, kept in a local SQLite database.</summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string? path = null)
    {
        path ??= Path.Combine(SettingsStore.AppDataDirectory, "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                finished_at TEXT NOT NULL,
                tool_id TEXT NOT NULL,
                file_count INTEGER NOT NULL,
                failed_count INTEGER NOT NULL,
                input_bytes INTEGER NOT NULL,
                output_bytes INTEGER NOT NULL,
                status TEXT NOT NULL,
                output_directory TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Add(Job job)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history (finished_at, tool_id, file_count, failed_count, input_bytes, output_bytes, status, output_directory)
            VALUES ($finishedAt, $toolId, $fileCount, $failedCount, $inputBytes, $outputBytes, $status, $outputDirectory);
            """;
        command.Parameters.AddWithValue("$finishedAt", (job.FinishedAt ?? DateTime.Now).ToString("O"));
        command.Parameters.AddWithValue("$toolId", job.Tool.Id);
        command.Parameters.AddWithValue("$fileCount", job.Inputs.Count);
        command.Parameters.AddWithValue("$failedCount", job.FailedCount);
        command.Parameters.AddWithValue("$inputBytes", job.InputBytes);
        command.Parameters.AddWithValue("$outputBytes", job.OutputBytes);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$outputDirectory", job.Output.Path);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> Recent(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, finished_at, tool_id, file_count, failed_count, input_bytes, output_bytes, status, output_directory
            FROM history ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(
                reader.GetInt64(0),
                DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                Enum.TryParse<JobStatus>(reader.GetString(7), out var status) ? status : JobStatus.Completed,
                reader.GetString(8)));
        }
        return entries;
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
