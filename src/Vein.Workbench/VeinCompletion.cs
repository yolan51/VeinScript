using System;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Vein.Workbench;

/// A completion entry for the shape/mark/event popup. Inserting it replaces the (usually empty)
/// segment after the just-typed sigil with the name.
public sealed class VeinCompletion : ICompletionData
{
    private readonly string _kind;

    public VeinCompletion(string name, string kind)
    {
        Text = name;
        _kind = kind;
    }

    public IImage? Image => null;
    public string Text { get; }

    // Return a styled control (not a bare string) so the label has an explicit foreground and stays
    // visible under the dark theme / blue selection.
    public object Content => new TextBlock { Text = Text, Foreground = Brushes.Gainsboro };

    public object Description => $"{_kind} {Text}";
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
