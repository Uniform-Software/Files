using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniFiles.Core.Jobs;
using UniFiles.Core.Tools;

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
    string OutputDirectory,
    string? ToolName = null,
    ToolPreset? Recipe = null);

/// <summary>
/// Past jobs, kept in a local SQLite database. Since version 2 (UniFiles 0.3) each entry also
/// keeps the action's name and its "recipe" (tool or chain steps, settings, destination) so it
/// can be run again.
/// </summary>
public sealed class HistoryStore
{
    private const int CurrentVersion = 2;

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
        Migrate(connection);
    }

    /// <summary>Brings an older database up to date; only the missing steps run.</summary>
    private static void Migrate(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(read.ExecuteScalar());
        if (version >= CurrentVersion)
            return;

        using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE history ADD COLUMN tool_name TEXT;
            ALTER TABLE history ADD COLUMN settings TEXT;
            PRAGMA user_version = 2;
            """;
        migrate.ExecuteNonQuery();
    }

    /// <summary>What ran, as a preset: the tool (or chain steps) and settings, destination included.</summary>
    public static ToolPreset Recipe(Job job) => job.Tool is ChainTool chain
        ? new ToolPreset
        {
            Name = chain.Name,
            ToolId = ChainTool.ChainId,
            Values = SavedSettings.ToSaved(job.Settings),
            Steps = chain.Steps.Select(s => new PresetStep { ToolId = s.Tool.Id, Values = SavedSettings.ToSaved(s.Settings) }).ToList(),
        }
        : new ToolPreset { Name = job.Tool.Name, ToolId = job.Tool.Id, Values = SavedSettings.ToSaved(job.Settings) };

    public void Add(Job job)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history (finished_at, tool_id, file_count, failed_count, input_bytes, output_bytes, status, output_directory, tool_name, settings)
            VALUES ($finishedAt, $toolId, $fileCount, $failedCount, $inputBytes, $outputBytes, $status, $outputDirectory, $toolName, $settings);
            """;
        command.Parameters.AddWithValue("$finishedAt", (job.FinishedAt ?? DateTime.Now).ToString("O"));
        command.Parameters.AddWithValue("$toolId", job.Tool.Id);
        command.Parameters.AddWithValue("$fileCount", job.Inputs.Count);
        command.Parameters.AddWithValue("$failedCount", job.FailedCount);
        command.Parameters.AddWithValue("$inputBytes", job.InputBytes);
        command.Parameters.AddWithValue("$outputBytes", job.OutputBytes);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$outputDirectory", job.Output.Path);
        command.Parameters.AddWithValue("$toolName", job.Tool.Name);
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(Recipe(job)));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> Recent(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, finished_at, tool_id, file_count, failed_count, input_bytes, output_bytes, status, output_directory, tool_name, settings
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
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : ReadRecipe(reader.GetString(10))));
        }
        return entries;
    }

    private static ToolPreset? ReadRecipe(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ToolPreset>(json);
        }
        catch (JsonException)
        {
            return null;
        }
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
