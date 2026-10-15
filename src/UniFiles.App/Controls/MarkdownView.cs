using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniFiles.App.Controls;

/// <summary>
/// Shows the little Markdown our own documents use (notices, release notes): headings, paragraphs,
/// lists, quotes, tables, rules, **bold**, `code` and links, which open in the browser.
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public MarkdownView()
    {
        Spacing = 10;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            Build(Markdown ?? "");
    }

    private void Build(string markdown)
    {
        Children.Clear();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
                Children.Add(Text(string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                FlushParagraph();
            }
            else if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var heading = Text(trimmed[level..].Trim());
                heading.Classes.Add(level == 1 ? "h1" : level == 2 ? "h2" : "label");
                heading.Margin = new Thickness(0, Children.Count == 0 ? 0 : level == 1 ? 14 : 8, 0, 0);
                Children.Add(heading);
            }
            else if (trimmed is "---" or "***")
            {
                FlushParagraph();
                Children.Add(new Border { Height = 1, Margin = new Thickness(0, 12), [!Border.BackgroundProperty] = this.GetResourceObservable("BorderBrush").ToBinding() });
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                FlushParagraph();
                var item = trimmed[2..];
                // Lines indented under the item belong to it.
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && lines[i + 1].Trim() is { Length: > 0 } next && !next.StartsWith("- "))
                    item += " " + lines[++i].Trim();
                Children.Add(Bullet(item));
            }
            else if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quote.Add(lines[i].TrimStart()[1..].Trim());
                i--;
                Children.Add(Quote(quote));
            }
            else if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    rows.Add(lines[i].Trim());
                i--;
                Children.Add(Table(rows));
            }
            else
            {
                paragraph.Add(trimmed);
            }
        }
        FlushParagraph();
    }

    private static Control Bullet(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), Margin = new Thickness(4, 0, 0, 0) };
        grid.Children.Add(new TextBlock { Text = "•", FontWeight = FontWeight.Bold });
        var body = Text(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private Control Quote(List<string> lines)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in string.Join("\n", lines).Split("\n\n"))
            panel.Children.Add(Text(block.Replace('\n', ' '), secondary: true));
        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("BorderBrush").ToBinding(),
            Child = panel,
        };
    }

    private Control Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var data = rows.Where(r => !SeparatorRow().IsMatch(r)).Select(Cells).ToList();
        var columns = data.Max(r => r.Length);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))) };
        for (var r = 0; r < data.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < data[r].Length; c++)
            {
                var text = Text(data[r][c], small: true);
                if (r == 0)
                    text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    Padding = new Thickness(0, 8, 14, 8),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    [!Border.BorderBrushProperty] = this.GetResourceObservable("BorderBrush").ToBinding(),
                    Child = text,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new Border { Margin = new Thickness(0, 4), Child = grid };
    }

    /// <summary>A wrapped paragraph with its bold parts, code and links.</summary>
    private static TextBlock Text(string text, bool small = false, bool secondary = false)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var links = new List<(int Start, int End, string Url)>();
        var length = 0;
        void Add(Run run)
        {
            block.Inlines!.Add(run);
            length += run.Text?.Length ?? 0;
        }
        if (small)
            block.FontSize = 13;
        if (secondary)
            block.Classes.Add("secondary");
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
                Add(new Run(text[position..match.Index]));
            if (match.Groups["bold"].Success)
                Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.Bold });
            else if (match.Groups["code"].Success)
                Add(new Run(match.Groups["code"].Value) { FontFamily = Monospace, FontSize = small ? 12 : 13 });
            else
            {
                // Links stay in the text (they wrap with it); a click on them opens the browser.
                var url = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["bare"].Value;
                var label = match.Groups["label"].Success ? match.Groups["label"].Value : url;
                links.Add((length, length + label.Length, url));
                Add(new Run(label) { TextDecorations = TextDecorations.Underline });
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            Add(new Run(text[position..]));
        if (links.Count > 0)
            MakeLinksClickable(block, links);
        return block;
    }

    private static readonly FontFamily Monospace = new(OperatingSystem.IsWindows() ? "Consolas" : "Menlo");

    private static void MakeLinksClickable(TextBlock block, List<(int Start, int End, string Url)> links)
    {
        string? LinkAt(Avalonia.Input.PointerEventArgs e)
        {
            var hit = block.TextLayout.HitTestPoint(e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top));
            if (!hit.IsInside)
                return null;
            var index = hit.TextPosition;
            return links.FirstOrDefault(l => index >= l.Start && index < l.End).Url;
        }

        block.PointerMoved += (_, e) => block.Cursor = LinkAt(e) is null ? null : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        block.PointerReleased += async (_, e) =>
        {
            if (LinkAt(e) is { } url && TopLevel.GetTopLevel(block) is { } top && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await top.Launcher.LaunchUriAsync(uri);
        };
    }

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>https?://[^)\s]+)\)|(?<bare>https?://[^\s)|]+[^\s)|.,;])")]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^\|?\s*:?-{3,}")]
    private static partial Regex SeparatorRow();
}
