using UniFiles.Core.Jobs;
using UniFiles.Core.Tools;

namespace UniFiles.Core.Tests;

public class OutputTargetTests
{
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uf-out"));
    private static readonly string Photos = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Photos", "Vacances"));

    [Fact]
    public void The_four_modes_give_the_expected_folders()
    {
        var input = new InputFile(Path.Combine(Photos, "IMG_1.jpg"));
        var date = new DateTime(2026, 10, 15);

        Assert.Equal(Base, new OutputTarget(OutputMode.Default, Base).For(input).Path);
        Assert.Equal(Photos, new OutputTarget(OutputMode.NextToOriginals, Base).For(input).Path);
        Assert.Equal(Path.Combine(Base, "2026-10-15"), new OutputTarget(OutputMode.DatedSubfolder, Base) { Date = date }.For(input).Path);
        Assert.Equal(Base, OutputTarget.Fixed(Base).For(input).Path);
    }

    [Fact]
    public void Keep_tree_reproduces_the_dropped_folder()
    {
        var input = new InputFile(Path.Combine(Photos, "Plage", "2024", "a.jpg"), Path.Combine("Plage", "2024"), "Vacances");

        Assert.Equal(Path.Combine(Base, "Vacances", "Plage", "2024"), new OutputTarget(OutputMode.Default, Base, KeepTree: true).For(input).Path);
        // Without the option, or next to the originals, the tree is ignored.
        Assert.Equal(Base, new OutputTarget(OutputMode.Default, Base).For(input).Path);
        Assert.Equal(Path.GetDirectoryName(input.Path), new OutputTarget(OutputMode.NextToOriginals, Base, KeepTree: true).For(input).Path);
    }

    [Fact]
    public void A_relative_path_cannot_leave_the_destination()
    {
        var input = new InputFile(Path.Combine(Photos, "a.jpg"), Path.Combine("..", "..", ".."), "Vacances");
        Assert.Equal(Base, new OutputTarget(OutputMode.Default, Base, KeepTree: true).For(input).Path);
    }

    [Fact]
    public void Expanding_a_folder_keeps_each_file_place()
    {
        using var temp = new TempFolder();
        var root = temp.Sub("Vacances");
        Directory.CreateDirectory(Path.Combine(root, "Plage"));
        File.WriteAllText(Path.Combine(root, "top.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "Plage", "deep.jpg"), "x");

        var inputs = FileKindDetector.ExpandInputs([root, temp.File("alone.jpg")]);

        Assert.Contains(inputs, i => i.Path.EndsWith("top.jpg") && i.RelativeDirectory == "" && i.RootName == "Vacances");
        Assert.Contains(inputs, i => i.Path.EndsWith("deep.jpg") && i.RelativeDirectory == "Plage" && i.RootName == "Vacances");
    }

    [Fact]
    public async Task Next_to_originals_never_overwrites()
    {
        using var temp = new TempFolder();
        var input = temp.File("doc.txt");
        File.WriteAllText(input, "original");
        var tool = new CopyTool();

        var job = new JobQueue().Enqueue(tool, [InputFile.Of(input)], new ToolSettings(), new OutputTarget(OutputMode.NextToOriginals, temp.Sub("unused")));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("original", File.ReadAllText(input));
        var output = Assert.Single(Assert.Single(job.Items).Outputs);
        Assert.Equal(Path.GetDirectoryName(input), Path.GetDirectoryName(output));
        Assert.EndsWith("doc (2).txt", output);
    }

    /// <summary>Writes a copy under the same name: the output folder picks a free one.</summary>
    private sealed class CopyTool : FileToolBase
    {
        public override string Id => "test.copy";
        public override ToolModule Module => ToolModule.Rename;
        public override string Name => "Copy";
        public override string Description => "";
        public override IReadOnlySet<FileKind> AcceptedKinds { get; } = new HashSet<FileKind>();
        public override IReadOnlyList<ToolOption> Options { get; } = [];

        public override async Task<IReadOnlyList<string>> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            [await WriteOutputAsync(context.Output, Path.GetFileName(context.Inputs[0]), path => File.WriteAllTextAsync(path, "copy", cancellationToken))];
    }
}
