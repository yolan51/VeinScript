using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The Tune tab: every shard var marked `tune("…")` (D21) in the program and in the bundles it needs — a
// library's too — with the value the project gives it in `vein.settings`. Only those: not the Workbench's
// own preferences (WorkbenchSettings), nor anything else called a setting.
//
// Change a value and it is saved to vein.settings, which holds only changed values (nobody has to fill
// it); a game running in Godot watches the file and takes the value at once. ↺ removes the line (the
// source's default again); → jumps to the declaration.
internal sealed class TunePanel : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 2, Margin = new Avalonia.Thickness(8, 6) };
    private string? _program;

    /// Open a file at a line. Wired by the window to the shared GoTo.
    public Action<string, int>? Navigate { get; set; }

    public TunePanel()
    {
        Content = new ScrollViewer { Content = _body };
    }

    /// List the tune vars of `program` (the run entry, else the open file).
    public void Update(string? program)
    {
        _program = program;
        _body.Children.Clear();
        if (program is null || !File.Exists(program))
        {
            Note("Save the file first: the tune vars are read from a program on disk.");
            return;
        }

        SettingsIndex.Result result;
        try { result = SettingsIndex.For(program); }
        catch (Exception ex) { Note("Could not read the tune vars: " + ex.Message); return; }

        if (result.Settings.Count == 0)
        {
            Note("No tune vars yet. Put  tune(\"what it changes\")  on the line above a shard var with a literal value; " +
                 "it is listed here, and a value you change is saved to vein.settings.");
            return;
        }
        int changed = result.Settings.Count(s => s.Value is not null);
        Note($"{result.Settings.Count} tune var(s), {changed} changed — {result.File.Path}");
        foreach (var problem in result.Problems)
            _body.Children.Add(new TextBlock { Text = problem.ToString(), Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        foreach (var section in result.Settings.GroupBy(s => s.Section))
        {
            _body.Children.Add(new TextBlock
            {
                Text = section.Key + (section.First().IsLibrary ? "   (library)" : ""),
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Avalonia.Thickness(0, 8, 0, 2),
            });
            foreach (var s in section) _body.Children.Add(Row(s, result.File.Path));
        }
    }

    private Control Row(SettingsIndex.Setting s, string file)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ToolTip.SetTip(row, $"{s.Doc}\n{s.Key} — {s.Type}\n{s.File}:{s.Line}");
        row.Children.Add(new TextBlock { Text = s.Name, Width = 220, VerticalAlignment = VerticalAlignment.Center });

        if (s.Type == "bool")
        {
            var box = new CheckBox { IsChecked = s.Current == "true" };
            box.IsCheckedChanged += (_, _) => Save(s, file, box.IsChecked == true ? "true" : "false");
            row.Children.Add(box);
        }
        else
        {
            var text = new TextBox { Text = s.Current, Width = 140, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
            if (s.Value is not null) text.Foreground = new SolidColorBrush(Color.Parse("#E5B567"));
            void Commit() { if (text.Text is { } t && t.Trim() != s.Current) Save(s, file, t.Trim()); }
            text.LostFocus += (_, _) => Commit();
            text.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            row.Children.Add(text);
        }

        row.Children.Add(new TextBlock { Text = "default " + s.Default, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        if (s.Value is not null)
        {
            var reset = new Button { Content = "↺", Padding = new Avalonia.Thickness(6, 0) };
            ToolTip.SetTip(reset, "Back to the default (remove the line from vein.settings)");
            reset.Click += (_, _) => { SettingsFile.Remove(file, s.Section, s.Name); Update(_program); };
            row.Children.Add(reset);
        }
        var go = new Button { Content = "→", Padding = new Avalonia.Thickness(6, 0) };
        ToolTip.SetTip(go, "Where it is declared");
        go.Click += (_, _) => Navigate?.Invoke(s.File, s.Line);
        row.Children.Add(go);
        row.Children.Add(new TextBlock { Text = s.Doc, Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void Save(SettingsIndex.Setting s, string file, string literal)
    {
        if (SettingsFile.ParseValue(literal, SettingsIndex.KindOf(s.Type), default) is null)
        {
            Note($"`{literal}` is not a {s.Type} — {s.Key} keeps {s.Current}.", Brushes.IndianRed);
            return;
        }
        SettingsFile.Set(file, s.Section, s.Name, literal);
        Update(_program);
    }

    private void Note(string text, IBrush? colour = null) =>
        _body.Children.Insert(0, new TextBlock { Text = text, Foreground = colour ?? Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
}
