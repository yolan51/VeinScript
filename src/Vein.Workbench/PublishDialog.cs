using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Cloud;

namespace Vein.Workbench;

// The last screen before source leaves the machine.
//
// PUBLISHING IS IRREVERSIBLE. Versions are immutable, there is no delete endpoint, and the catalogue is
// public — so this shows everything a person would want to have known afterwards, and sends nothing
// until they press the button. `Publisher.Prepare` does the whole job with no network at all, which is
// what makes a review screen possible rather than a progress bar with a cancel that comes too late.
//
// Four things are worth their space here, and each one exists because getting it wrong is quiet:
//
//   THE NAMES. `yolan.Combat.shards.Boot.vein`, not `Boot.vein`. Every bundle the scaffold makes
//     contains a `shards/Boot.vein`, so under bare filenames the second project published would
//     overwrite the first and the service would report success.
//   THE REDACTIONS. What was replaced, and where. The local file keeps the real key; the published copy
//     does not, and nobody should discover that difference later.
//   THE SKIPPED. "17 files" and "17 files, one of which was quietly left behind" are different
//     publishes, and only one of them compiles when somebody restores it.
//   THE FOREIGN AUTHORS. A `by` line that is not yours, offered as a fact rather than rewritten.
internal sealed class PublishDialog : Window
{
    private readonly PublishPlan _plan;
    private readonly TextBox _description;
    private readonly Button _publish = new() { Content = "Publish", IsDefault = true };
    private readonly TextBlock _error = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#F48771")),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    private readonly CloudSession _session;
    private bool _busy;
    private PublishOutcome? _result;

    private PublishDialog(CloudSession session, PublishPlan plan)
    {
        _session = session;
        _plan = plan;

        Title = "Publish to VeinScript";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _description = new TextBox
        {
            Text = plan.Description,
            Watermark = "One line: what this bundle is for",
            MaxLength = 200
        };

        _publish.Click += (_, _) => _ = SendAsync();
        _publish.IsEnabled = plan.CanPublish;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var body = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(18, 14) };

        body.Children.Add(Header());
        body.Children.Add(Compile());
        if (plan.ForeignAuthors.Count > 0) body.Children.Add(ForeignAuthors());
        if (plan.Redactions.Count > 0) body.Children.Add(Redactions());
        body.Children.Add(Files());
        if (plan.Skipped.Count > 0) body.Children.Add(Skipped());

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Bottom(new StackPanel
                {
                    Margin = new Avalonia.Thickness(18, 8, 18, 14),
                    Spacing = 8,
                    Children =
                    {
                        _error,
                        new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                        _description,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { _publish, cancel }
                        }
                    }
                }),
                new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }
        };
    }

    private static Control Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }

    private Control Header() => Section(
        $"{_plan.BundleName}",
        $"{_plan.Files.Count} file(s), {_plan.Package.TotalBytes / 1024} KB, published as {_plan.Author}. " +
        "This is public and cannot be deleted.");

    private Control Compile()
    {
        bool ok = _plan.Check.Ok;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Badge(
            ok ? "compiles" : $"{_plan.Check.Errors} error(s) — cannot publish",
            ok ? "#4EC9B0" : "#F48771"));

        if (_plan.Check.Warnings > 0)
            panel.Children.Add(Muted($"{_plan.Check.Warnings} warning(s) — recorded, not a refusal."));

        if (!ok) panel.Children.Add(Mono(_plan.Check.Summary(), "#F48771"));

        return Group("Compile", panel);
    }

    private Control ForeignAuthors() => Group("Author", new StackPanel
    {
        Spacing = 4,
        Children =
        {
            Badge("a `by` line is not yours", "#D7BA7D"),
            Muted($"This package declares: {string.Join(", ", _plan.ForeignAuthors)}. You are publishing " +
                  $"as {_plan.Author}. The source is not changed — edit the `by` line yourself if it is wrong.")
        }
    });

    private Control Redactions()
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(Muted("Replaced in the uploaded copy only. Your files keep the real values."));

        foreach (var f in _plan.Redactions)
            panel.Children.Add(Mono($"{f.Path}:{f.Line}   {f.Preview}  ->  {f.Placeholder}", "#D7BA7D"));

        return Group($"Secrets redacted ({_plan.Redactions.Count})", panel);
    }

    private Control Files()
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var (name, source, path) in _plan.Files)
            panel.Children.Add(Mono($"{name}      <-  {path}   ({source.Length} chars)", "#9CDCFE"));

        return Group($"Files ({_plan.Files.Count})", panel);
    }

    private Control Skipped()
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var s in _plan.Skipped)
            panel.Children.Add(Mono($"{s.Path}   —  {s.Reason}", "#8A8A8A"));

        return Group($"Not published ({_plan.Skipped.Count})", panel);
    }

    // ---- pieces ------------------------------------------------------------------------------------

    private static Control Group(string heading, Control inner) => new StackPanel
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = heading.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")) },
            new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Padding = new Avalonia.Thickness(10, 8),
                CornerRadius = new Avalonia.CornerRadius(3),
                Child = new ScrollViewer { Content = inner, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                           MaxHeight = 200 }
            }
        }
    };

    private static Control Section(string title, string blurb) => new StackPanel
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold,
                            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") },
            Muted(blurb)
        }
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#A0A0A0"))
    };

    private static Control Mono(string text, string colour) => new SelectableTextBlock
    {
        Text = text, FontSize = 11.5,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        Foreground = new SolidColorBrush(Color.Parse(colour)),
        TextWrapping = TextWrapping.NoWrap
    };

    private static Control Badge(string text, string colour) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        BorderBrush = new SolidColorBrush(Color.Parse(colour)),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(2),
        Padding = new Avalonia.Thickness(6, 2),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(colour)) }
    };

    // ---- sending -----------------------------------------------------------------------------------

    private async Task SendAsync()
    {
        if (_busy) return;
        _busy = true;
        _publish.Content = "Publishing…";
        _publish.IsEnabled = false;

        try
        {
            var plan = _plan with { Description = (_description.Text ?? "").Trim() };
            _result = await Publisher.PublishAsync(_session, plan);
            Close();
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
            _error.IsVisible = true;
            _publish.Content = "Publish";
            _publish.IsEnabled = true;
        }
        finally { _busy = false; }
    }

    /// Null when cancelled or when nothing was sent.
    public static async Task<PublishOutcome?> ShowAsync(Window owner, CloudSession session, PublishPlan plan)
    {
        var dialog = new PublishDialog(session, plan);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
