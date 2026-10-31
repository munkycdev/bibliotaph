using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Bibliotaph.App.Controls;

/// <summary>
/// A section of a book's details under a heading that folds it away, with the section's own links on the right while
/// it's open. Its look is the implicit style in Themes/Controls.xaml.
/// </summary>
public sealed class DetailSection : HeaderedContentControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DetailSection),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(DetailSection),
        new PropertyMetadata(null, OnActionsChanged));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    // A logical child, so the links bind, find resources and are named in the page like the section's content.
    static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var section = (DetailSection)d;
        if (e.OldValue is not null) section.RemoveLogicalChild(e.OldValue);
        if (e.NewValue is not null) section.AddLogicalChild(e.NewValue);
    }

    protected override IEnumerator LogicalChildren
    {
        get
        {
            var children = new List<object>();
            var inner = base.LogicalChildren;
            while (inner?.MoveNext() == true) children.Add(inner.Current);
            if (Actions is not null) children.Add(Actions);
            return children.GetEnumerator();
        }
    }
}
