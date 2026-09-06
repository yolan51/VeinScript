using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;

namespace Vein.Workbench;

// WHY: the Workbench held exactly one file. `_currentPath` was a single string, so opening a fragment
// to check a shape closed the bundle you were reading it for — and the programs this IDE exists to edit
// are not single files. samples/chat/ is three, an app with capabilities is four or more, a site is a
// main file plus its shards.
//
// ONE EDITOR, MANY DOCUMENTS. Every tab owns an AvaloniaEdit TextDocument and the window swaps
// `Editor.Document` when you switch. That is what makes undo per-file for free: the undo stack lives on
// the document, so Ctrl+Z in one tab cannot eat an edit you made in another — which it would if tabs
// were just remembered strings pushed through one shared editor.
//
// Built in code rather than XAML for the same reason PromptDialog is: a strip of buttons is not worth a
// second file, and the dirty marker needs per-tab logic anyway.
internal sealed class EditorTabs : UserControl
{
    /// One open file. `Path` is null for a never-saved buffer.
    internal sealed class Doc
    {
        public string? Path { get; set; }
        public required TextDocument Document { get; init; }
        public bool Dirty { get; set; }

        /// Where the caret was when you last left this tab, so coming back lands where you were.
        public int Caret { get; set; }

        public string Name => Path is null ? "untitled.vein" : System.IO.Path.GetFileName(Path);
    }

    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Doc> _docs = new();

    /// A different tab became current. The window swaps the editor's document.
    /// Raised on every tab switch, and with NULL when the last tab closes and nothing is open.
    public event Action<Doc?>? Activated;

    /// A tab is about to close and has unsaved edits — return true to proceed with closing.
    public Func<Doc, Task<bool>>? ConfirmClose;

    public Doc? Active { get; private set; }
    public IReadOnlyList<Doc> Docs => _docs;
    public IEnumerable<Doc> DirtyDocs => _docs.Where(d => d.Dirty);

    public EditorTabs()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _strip
        };
    }

    /// Open `path` — or focus the tab that already has it. Reusing is the whole point: double-clicking
    /// the explorer twice must not give you two tabs on one file that can then disagree.
    public Doc Open(string? path, string text)
    {
        if (path is not null &&
            _docs.FirstOrDefault(d => d.Path is not null && PathEquals(d.Path, path)) is { } existing)
        {
            Activate(existing);
            return existing;
        }

        var doc = new Doc { Path = path, Document = new TextDocument(text) };

        // The document's own TextChanged is the only reliable dirty signal: it fires for typing, paste,
        // undo and redo alike, and it belongs to the document rather than the editor — so a tab left in
        // the background still tracks its own state.
        doc.Document.TextChanged += (_, _) => { if (!doc.Dirty) { doc.Dirty = true; Refresh(); } };

        _docs.Add(doc);
        Activate(doc);
        return doc;
    }

    /// Activate a document, or null for "nothing is open" — the state after the last tab is closed.
    public void Activate(Doc? doc)
    {
        if (doc is not null && !_docs.Contains(doc)) return;
        Active = doc;
        Refresh();
        Activated?.Invoke(doc);
    }

    /// Mark the active document saved. Called after a successful write.
    public void MarkSaved(Doc doc, string? path = null)
    {
        if (path is not null) doc.Path = path;
        doc.Dirty = false;
        Refresh();
    }

    /// Close a tab, asking first when it has unsaved edits. Closing the last one leaves nothing open.
    public async Task CloseAsync(Doc doc)
    {
        if (doc.Dirty && ConfirmClose is not null && !await ConfirmClose(doc)) return;

        int at = _docs.IndexOf(doc);
        _docs.Remove(doc);

        // CLOSING THE LAST TAB LEAVES NOTHING OPEN. It used to reopen an empty buffer immediately, so
        // "close everything" always left one `untitled.vein` behind that could not be got rid of —
        // close it and another appeared. The window handles a null active document by emptying the
        // editor, which is the honest picture of a workbench with no file in it.
        if (_docs.Count == 0) { Activate(null); return; }

        if (ReferenceEquals(Active, doc)) Activate(_docs[Math.Clamp(at, 0, _docs.Count - 1)]);
        else Refresh();
    }

    /// Close a tab WITHOUT asking about unsaved changes.
    ///
    /// For one case only: the file behind it has been deleted. `CloseAsync` would offer to save it
    /// first, and taking that offer writes the deleted file straight back — so the prompt is not a
    /// safety net here, it is a way to undo the delete by accident.
    public void Discard(Doc doc)
    {
        int at = _docs.IndexOf(doc);
        if (at < 0) return;

        _docs.Remove(doc);

        if (_docs.Count == 0) { Activate(null); return; }
        if (ReferenceEquals(Active, doc)) Activate(_docs[Math.Clamp(at, 0, _docs.Count - 1)]);
        else Refresh();
    }

    /// Rebuild the strip. Small enough that recreating beats tracking per-button state.
    private void Refresh()
    {
        _strip.Children.Clear();

        foreach (var doc in _docs)
        {
            bool active = ReferenceEquals(doc, Active);

            var label = new TextBlock
            {
                // The dot is the dirty marker: present or absent, in the same place, so a glance at the
                // strip says what would be lost. A changed colour alone reads as decoration.
                Text = (doc.Dirty ? "● " : "") + doc.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? Brushes.White : Brushes.Gainsboro,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal
            };

            var close = new Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Avalonia.Thickness(4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            close.Click += async (_, e) => { e.Handled = true; await CloseAsync(doc); };

            var tab = new Button
            {
                Padding = new Avalonia.Thickness(10, 4),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, active ? 2 : 0),
                BorderBrush = Brushes.MediumPurple,
                Background = active ? new SolidColorBrush(Color.Parse("#2D2D30")) : Brushes.Transparent,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { label, close }
                }
            };
            ToolTip.SetTip(tab, doc.Path ?? "not saved yet");
            tab.Click += (_, _) => Activate(doc);

            _strip.Children.Add(tab);
        }
    }

    /// Windows paths are case-insensitive; comparing them ordinally would open a second tab for the
    /// same file reached through the explorer versus the file picker.
    private static bool PathEquals(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
