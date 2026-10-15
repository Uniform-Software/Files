using Microsoft.Data.Sqlite;
using UniFiles.Core.Jobs;
using UniFiles.Core.Storage;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Tests;

/// <summary>Data written by UniFiles 0.2 must still load in 0.3.</summary>
public class MigrationTests
{
    [Fact]
    public async Task A_version_1_history_gets_the_new_columns_and_keeps_its_rows()
    {
        using var temp = new TempFolder();
        var path = temp.File("history.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, finished_at TEXT NOT NULL, tool_id TEXT NOT NULL,
                    file_count INTEGER NOT NULL, failed_count INTEGER NOT NULL, input_bytes INTEGER NOT NULL,
                    output_bytes INTEGER NOT NULL, status TEXT NOT NULL, output_directory TEXT NOT NULL);
                INSERT INTO history (finished_at, tool_id, file_count, failed_count, input_bytes, output_bytes, status, output_directory)
                VALUES ('2026-10-01T10:00:00.0000000+02:00', 'image.compress', 3, 0, 1000, 400, 'Completed', '/out');
                """;
            command.ExecuteNonQuery();
        }

        var store = new HistoryStore(path);
        var input = temp.File("a.txt");
        File.WriteAllText(input, "x");
        var job = new JobQueue().Enqueue(new UniFiles.Core.Tools.Rename.BatchRenameTool(), [input], ToolSettings.FromDefaults(new UniFiles.Core.Tools.Rename.BatchRenameTool()), new OutputFolder(temp.Sub("out")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        store.Add(job);

        var entries = store.Recent();
        Assert.Equal(2, entries.Count);
        var old = entries.Single(e => e.ToolId == "image.compress");
        Assert.Null(old.Recipe);
        var recent = entries.Single(e => e.ToolId == "rename.pattern");
        Assert.Equal("rename.pattern", recent.Recipe?.ToolId);
        Assert.NotNull(recent.ToolName);

        // Opening it again doesn't migrate twice.
        Assert.Equal(2, new HistoryStore(path).Recent().Count);
    }

    [Fact]
    public void Settings_from_0_2_2_load_with_their_presets()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """
            {
              "OutputDirectory": "/Users/x/Documents/UniFiles",
              "Theme": "Light",
              "Language": "fr",
              "HardwareAcceleration": true,
              "CheckForUpdates": false,
              "ContextMenuEnabled": true,
              "ToolSettings": { "image.compress": { "quality": 70, "strip": true } },
              "Presets": [
                { "Id": "abc123def456", "Name": "Web", "ToolId": "image.resize", "Values": { "maxWidth": 1600 } }
              ]
            }
            """);

        var settings = new SettingsStore(temp.File("settings.json")).Load();

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.False(settings.CheckForUpdates);
        var preset = Assert.Single(settings.Presets);
        Assert.False(preset.IsChain);
        Assert.Empty(settings.WatchRules);
        var (tool, values) = PresetResolver.Resolve(new ToolCatalog(), preset)!.Value;
        Assert.Equal("image.resize", tool.Id);
        Assert.Equal(1600, values.GetInt("maxWidth"));
        // No destination saved: the default one.
        Assert.Equal(OutputMode.Default, OutputSettings.Read(values, settings.OutputDirectory).Mode);
    }
}
