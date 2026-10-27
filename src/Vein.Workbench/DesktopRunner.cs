using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vein.Compiler.Ir;

namespace Vein.Workbench;

/// ▶ for a program that opens WINDOWS (it needs the stdlib's Vein.Desktop): the program's identities drawn
/// as native controls, and what the user does turned into its events.
///
/// PLUMBING ONLY. What a click means, what a removal takes with it and what any window shows are the
/// program's, in VeinScript. This class does the three things VeinScript cannot: it owns real windows, it
/// draws each identity as a control, and it reports what happens to those controls. The host contract it
/// keeps is docs/HOSTING.md's ("Desktop programs"):
///   * typing writes the box's $Field.value, THEN raises @Edited — the way a host writes $View;
///   * a tick writes $Toggle.checked, then raises @Toggled; Enter raises @Submitted; a click, @Clicked;
///   * closing a window raises @WindowClosed, then removes the window and everything parented under it.
///
/// Drawing is a reconcile. Every frame the tree is read again from the world — windows are identities with
/// $Window, widgets hang under their *Vein.Core.Relations.$Parent in spawn order (D13) — and each identity
/// keeps its control by id: created once, updated in place, freed when the identity is gone.
///
/// On the INTERPRETER, in-process, as the Runtime tab runs it: no Vein.Hosting, so no Roslyn in the
/// Workbench for this.
internal sealed class DesktopRunner
{
    private readonly Interp _interp;
    private readonly IrModule _module;
    private readonly string _programFolder;   // where `Picture(…, "logo.png")` and a window icon are looked up
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _ended;
    private readonly Dictionary<long, Window> _windows = new();
    private readonly Dictionary<long, Control> _controls = new();
    private readonly DispatcherTimer _timer;
    private DateTime _last = DateTime.Now;
    private bool _syncing;   // the runner is writing a control: its change event is the runner's, not the user's
    private bool _done;

    public bool Running => !_done;
    public IReadOnlyDictionary<long, Window> Windows => _windows;
    public IReadOnlyList<Node> Tree { get; private set; } = Array.Empty<Node>();

    public DesktopRunner(IrModule module, string baseDirectory, string programFolder, Action<string> print, Action<string> ended)
    {
        _module = module;
        _programFolder = programFolder;
        _ended = ended;
        var lines = new LineSink(print);
        _interp = new Interp { BaseDirectory = baseDirectory, Output = lines, ErrorOutput = lines };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, (_, _) => Frame());
    }

    public void Start()
    {
        if (!Guard(() => _interp.Boot(_module))) return;
        Render();
        if (_done) return;
        if (_windows.Count == 0) { Quit("it opened no window — bring a Window(…) in `run once`"); return; }
        _timer.Start();
    }

    public void Stop() => Quit("stopped");

    // ---- what the user does → the program's events --------------------------------------------------
    // The stdlib keeps bare event names (Lower.EventKey), so Vein.Desktop's events are raised as `Edited`.
    // `FireHost` answers false for an event the program never hears — nothing to deliver, which is fine.

    public void Click(long id) => Input(() => _interp.FireClicked(id));

    public void Edit(long id, string text) => Input(() =>
    {
        _interp.World.Publish(id, "Field", "value", text);
        _interp.FireHost("Edited", Payload(("target", id), ("value", text)));
    });

    public void Submit(long id, string text) => Input(() => _interp.FireHost("Submitted", Payload(("target", id), ("value", text))));

    public void Toggle(long id, bool on) => Input(() =>
    {
        _interp.World.Publish(id, "Toggle", "checked", on);
        _interp.FireHost("Toggled", Payload(("target", id), ("checked", on)));
    });

    public void Pick(long id, int picked, string option) => Input(() =>
    {
        _interp.World.Publish(id, "Choice", "picked", (long)picked);
        _interp.FireHost("Picked", Payload(("target", id), ("picked", (long)picked), ("option", option)));
    });

    public void CloseWindow(long id) => Input(() =>
    {
        _interp.FireHost("WindowClosed", Payload(("target", id)));
        foreach (long gone in Descendants(id)) _interp.World.Destroy(gone);
        _interp.World.Destroy(id);
    });

    private void Input(Action raise)
    {
        if (_done) return;
        if (Guard(raise)) Render();
    }

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);

    /// Everything parented under `id`, at any depth — read from the world, so it includes identities that
    /// are not widgets. Children before parents.
    private List<long> Descendants(long id)
    {
        var byParent = _interp.World.Snapshot()
            .Where(e => ParentOf(e) is not null)
            .GroupBy(e => ParentOf(e)!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Entity).ToList());
        var found = new List<long>();
        void Walk(long p, int depth)
        {
            if (depth > 64 || !byParent.TryGetValue(p, out var kids)) return;
            foreach (long k in kids) { Walk(k, depth + 1); found.Add(k); }
        }
        Walk(id, 0);
        return found;
    }

    // ---- the frame ----------------------------------------------------------------------------------

    private void Frame()
    {
        var now = DateTime.Now;
        double delta = Math.Min((now - _last).TotalSeconds, 0.25);
        _last = now;
        if (Guard(() => { _interp.FireTicked(_interp.Tick + 1, delta); _interp.Frame(); })) Render();
    }

    /// A program fault ends the run with what it said, not the Workbench.
    private bool Guard(Action step)
    {
        try { step(); return true; }
        catch (Exception ex) { Quit("runtime error: " + ex.Message); return false; }
    }

    public void Render()
    {
        if (_done) return;
        if (_interp.Stopped) { Quit("it quit"); return; }

        Tree = Read(_interp.World.Snapshot());
        var seen = new HashSet<long>();
        _syncing = true;
        try
        {
            foreach (var w in Tree)
            {
                seen.Add(w.Id);
                Fill((Panel)((ScrollViewer)WindowFor(w).Content!).Content!, w, seen);
            }
        }
        finally { _syncing = false; }

        foreach (long id in _controls.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Detach(_controls[id]);
            _controls.Remove(id);
        }
        foreach (long id in _windows.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var window = _windows[id];
            _windows.Remove(id);   // first, so its Closing handler knows the program closed it
            window.Close();
        }
        if (_windows.Count == 0 && _timer.IsEnabled) Quit("its last window closed");
    }

    private Window WindowFor(Node w)
    {
        if (_windows.TryGetValue(w.Id, out var window))
        {
            if (window.Title != w.Title) window.Title = w.Title;
            return window;
        }

        long id = w.Id;
        window = new Window
        {
            Title = w.Title, Width = w.Width, Height = w.Height,
            Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Spacing = 8 } },
        };
        if (w.Icon.Length > 0 && Resolve(w.Icon) is { } iconPath && File.Exists(iconPath))
        {
            try { window.Icon = new WindowIcon(iconPath); } catch (Exception) { /* not an image: no icon, not a crash */ }
        }
        window.Closing += (_, _) =>
        {
            if (_windows.Remove(id)) CloseWindow(id);   // the user closed it; the program hears it first
        };
        _windows[id] = window;
        window.Show();
        return window;
    }

    private void Fill(Panel panel, Node node, HashSet<long> seen)
    {
        var wanted = new List<Control>(node.Children.Count);
        foreach (var child in node.Children)
        {
            seen.Add(child.Id);
            var control = ControlFor(child);
            if (control is Panel inner) Fill(inner, child, seen);
            wanted.Add(control);
        }
        if (panel.Children.SequenceEqual(wanted)) return;

        panel.Children.Clear();
        foreach (var control in wanted)
        {
            Detach(control);
            panel.Children.Add(control);
        }
    }

    /// `#Hidden` (Vein.Graphics) keeps the control and draws it nowhere — and a hidden panel hides what is in it.
    private Control ControlFor(Node n)
    {
        var control = Build(n);
        if (control.IsVisible == n.Hidden) control.IsVisible = !n.Hidden;
        Paint(control, n.Tint);
        return control;
    }

    /// `*Vein.Graphics.Looks.$Tint` is a widget's COLOUR, as it is a picture's: a button's or a panel's
    /// background, the colour of text. `a` is how strong it is, so one hue at four `a`s is four intensities.
    /// Without one, the theme's own colour comes back.
    private static void Paint(Control control, Color? tint)
    {
        var property = control switch
        {
            CheckBox => Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,   // before Button: a CheckBox IS one
            Button or ComboBox => Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            TextBlock => TextBlock.ForegroundProperty,
            _ => null,
        };
        if (property is null) return;
        if (tint is { } c)
        {
            if (control.GetValue(property) is not SolidColorBrush { Color: var now } || now != c)
                control.SetValue(property, new SolidColorBrush(c));
        }
        else if (control.IsSet(property)) control.ClearValue(property);
    }

    private Control Build(Node n)
    {
        _controls.TryGetValue(n.Id, out var existing);
        long id = n.Id;
        switch (n.Kind)
        {
            case Kind.Panel:
                var stack = existing as StackPanel ?? Replace(id, new StackPanel());
                stack.Orientation = n.Row ? Orientation.Horizontal : Orientation.Vertical;
                stack.Spacing = n.Gap;
                return stack;

            case Kind.Button:
                if (existing is not Button button)
                {
                    button = Replace(id, new Button());
                    button.Click += (_, _) => Click(id);
                }
                if (!Equals(button.Content, n.Text)) button.Content = n.Text;
                button.Classes.Set("accent", n.Accent);   // Fluent's accent button, for `#Accent`
                return button;

            case Kind.Choice:
                if (existing is not ComboBox combo)
                {
                    combo = Replace(id, new ComboBox { MinWidth = 110 });
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (!_syncing && combo.SelectedIndex >= 0)
                            Pick(id, combo.SelectedIndex, combo.SelectedItem as string ?? "");
                    };
                }
                // The options are the program's list: replaced only when it changed, so an open dropdown and its
                // selection are not reset every frame.
                if (combo.ItemsSource is not IReadOnlyList<string> shown || !shown.SequenceEqual(n.Options))
                    combo.ItemsSource = n.Options;
                int picked = n.Options.Count == 0 ? -1 : Math.Clamp(n.Picked, 0, n.Options.Count - 1);
                if (combo.SelectedIndex != picked) combo.SelectedIndex = picked;
                return combo;

            case Kind.Picture:
                var image = existing as Avalonia.Controls.Image ?? Replace(id, new Avalonia.Controls.Image { Stretch = Stretch.Uniform });
                image.Height = n.Height;
                var bitmap = ImageAt(n.Source);
                if (!ReferenceEquals(image.Source, bitmap)) image.Source = bitmap;
                return image;

            case Kind.Input:
                if (existing is not TextBox box)
                {
                    box = Replace(id, new TextBox { MinWidth = 220 });
                    box.TextChanged += (_, _) => { if (!_syncing) Edit(id, box.Text ?? ""); };
                    box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(id, box.Text ?? ""); };
                }
                box.Watermark = n.Placeholder;
                if ((box.Text ?? "") != n.Value)
                {
                    box.Text = n.Value;
                    box.CaretIndex = n.Value.Length;
                }
                return box;

            case Kind.Check:
                if (existing is not CheckBox check)
                {
                    check = Replace(id, new CheckBox());
                    check.IsCheckedChanged += (_, _) => { if (!_syncing) Toggle(id, check.IsChecked == true); };
                }
                if (!Equals(check.Content, n.Text)) check.Content = n.Text;
                if (check.IsChecked != n.Checked) check.IsChecked = n.Checked;
                return check;

            default:
                var text = existing as TextBlock ?? Replace(id, new TextBlock { TextWrapping = TextWrapping.Wrap });
                text.Text = n.Text;
                if (n.FontSize > 0) text.FontSize = n.FontSize; else text.ClearValue(TextBlock.FontSizeProperty);
                return text;
        }
    }

    /// A relative path means beside the PROGRAM — the folder its `.vein` is in — so a sample and its picture
    /// travel together, whatever folder the Workbench has open.
    private string? Resolve(string path) =>
        path.Length == 0 ? null : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_programFolder, path));

    /// Loaded once per path. A missing or unreadable file draws nothing rather than ending the program.
    private Avalonia.Media.Imaging.Bitmap? ImageAt(string source)
    {
        if (Resolve(source) is not { } path) return null;
        if (_images.TryGetValue(path, out var cached)) return cached;
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        try { if (File.Exists(path)) bitmap = new Avalonia.Media.Imaging.Bitmap(path); } catch (Exception) { }
        return _images[path] = bitmap;
    }

    /// An identity whose kind changed gets a new control; the old one leaves the window.
    private T Replace<T>(long id, T control) where T : Control
    {
        if (_controls.TryGetValue(id, out var old)) Detach(old);
        _controls[id] = control;
        return control;
    }

    private static void Detach(Control control)
    {
        if (control.Parent is Panel parent) parent.Children.Remove(control);
    }

    private void Quit(string reason)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        try { _interp.Shutdown(); } catch (Exception) { /* ending anyway */ }
        foreach (var window in _windows.Values.ToList()) window.Close();
        _windows.Clear();
        foreach (var bitmap in _images.Values) bitmap?.Dispose();
        _images.Clear();
        _ended(reason);
    }

    // ---- reading the world as a tree ------------------------------------------------------------------

    internal enum Kind { Window, Panel, Button, Input, Check, Choice, Picture, Text }

    /// One window or widget, read from one identity. Only the fields its kind uses mean anything.
    internal sealed record Node(long Id, Kind Kind, IReadOnlyList<Node> Children)
    {
        public string Title { get; init; } = "";
        public double Width { get; init; }
        public double Height { get; init; }
        public double Gap { get; init; }
        public bool Row { get; init; }
        public string Text { get; init; } = "";
        public double FontSize { get; init; }
        public string Value { get; init; } = "";
        public string Placeholder { get; init; } = "";
        public bool Checked { get; init; }
        public string Icon { get; init; } = "";
        public string Source { get; init; } = "";
        public bool Hidden { get; init; }
        public bool Accent { get; init; }
        public Color? Tint { get; init; }
        public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
        public int Picked { get; init; }

        public IEnumerable<Node> Walk() => Children.SelectMany(c => c.Walk()).Prepend(this);
    }

    /// Windows are identities with $Window; a widget hangs under its $Parent, in spawn order. An identity that
    /// is not a widget, or whose parent is gone, is not drawn: the program's other identities share the world.
    internal static IReadOnlyList<Node> Read(IReadOnlyList<EntityStore.EntitySnapshot> world)
    {
        var children = new Dictionary<long, List<EntityStore.EntitySnapshot>>();
        foreach (var e in world.OrderBy(e => e.Entity))
            if (KindOf(e) is { } k && k != Kind.Window && ParentOf(e) is long parent)
                (children.TryGetValue(parent, out var list) ? list : children[parent] = new()).Add(e);

        return world.Where(e => KindOf(e) == Kind.Window).OrderBy(e => e.Entity)
                    .Select(w => NodeOf(w, children, depth: 0)).ToList();
    }

    private static Node NodeOf(EntityStore.EntitySnapshot e, Dictionary<long, List<EntityStore.EntitySnapshot>> children, int depth)
    {
        // The builders cannot make a $Parent cycle, but a program can attach anything: stop, don't recurse forever.
        var kids = depth < 64 && children.TryGetValue(e.Entity, out var list)
            ? list.Select(c => NodeOf(c, children, depth + 1)).ToList()
            : new List<Node>();

        var kind = KindOf(e)!.Value;
        var node = kind switch
        {
            Kind.Window => new Node(e.Entity, kind, kids)
            {
                Title = Text(e, "Window", "title"), Width = Number(e, "Window", "width", 480), Height = Number(e, "Window", "height", 360),
                Icon = Text(e, "Window", "icon"),
            },
            Kind.Panel => new Node(e.Entity, kind, kids) { Gap = Number(e, "Panel", "gap", 8), Row = e.Marks.Contains("Row") },
            Kind.Button => new Node(e.Entity, kind, kids) { Text = Text(e, "Button", "label"), Accent = e.Marks.Contains("Accent") },
            Kind.Input => new Node(e.Entity, kind, kids) { Value = Text(e, "Field", "value"), Placeholder = Text(e, "Field", "placeholder") },
            Kind.Check => new Node(e.Entity, kind, kids) { Text = Text(e, "Toggle", "label"), Checked = Field(e, "Toggle", "checked") is true },
            Kind.Picture => new Node(e.Entity, kind, kids) { Source = Text(e, "Image", "source"), Height = Number(e, "Picture", "height", 32) },
            Kind.Choice => new Node(e.Entity, kind, kids)
            {
                Options = Field(e, "Choice", "options") is System.Collections.IEnumerable items
                    ? items.Cast<object?>().Select(o => o?.ToString() ?? "").ToList()
                    : Array.Empty<string>(),
                Picked = (int)Number(e, "Choice", "picked", 0),
            },
            _ => new Node(e.Entity, kind, kids) { Text = Text(e, "Text", "content"), FontSize = Number(e, "Font", "size", 0) },
        };
        return node with { Hidden = e.Marks.Contains("Hidden"), Tint = TintOf(e) };
    }

    private static Color? TintOf(EntityStore.EntitySnapshot e)
    {
        if (!e.Components.ContainsKey("Tint")) return null;
        byte Channel(string f) => (byte)Math.Round(Math.Clamp(Number(e, "Tint", f, 1), 0, 1) * 255);
        return Color.FromArgb(Channel("a"), Channel("r"), Channel("g"), Channel("b"));
    }

    private static Kind? KindOf(EntityStore.EntitySnapshot e) =>
        e.Components.ContainsKey("Window") ? Kind.Window
        : e.Components.ContainsKey("Panel") ? Kind.Panel
        : e.Components.ContainsKey("Button") ? Kind.Button
        : e.Components.ContainsKey("Field") ? Kind.Input
        : e.Components.ContainsKey("Toggle") ? Kind.Check
        : e.Components.ContainsKey("Choice") ? Kind.Choice
        : e.Components.ContainsKey("Image") ? Kind.Picture
        : e.Components.ContainsKey("Text") ? Kind.Text
        : null;

    private static object? Field(EntityStore.EntitySnapshot e, string shape, string field) =>
        e.Components.TryGetValue(shape, out var fields) && fields.TryGetValue(field, out var v) ? v : null;

    private static string Text(EntityStore.EntitySnapshot e, string shape, string field) => Field(e, shape, field)?.ToString() ?? "";

    private static double Number(EntityStore.EntitySnapshot e, string shape, string field, double fallback) =>
        Field(e, shape, field) switch { long l => l, int i => i, double d => d, float f => f, _ => fallback };

    private static long? ParentOf(EntityStore.EntitySnapshot e) =>
        Field(e, "Parent", "of") is { } of ? Convert.ToInt64(of) : null;

    /// The program's @Print lines, one at a time.
    private sealed class LineSink(Action<string> line) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') { line(_buffer.ToString().TrimEnd('\r')); _buffer.Clear(); }
            else _buffer.Append(value);
        }
    }
}
