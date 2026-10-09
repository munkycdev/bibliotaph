namespace Bibliotaph.App.Views;

/// <summary>The bulk metadata editor, over the Library (slice 4e). The Library moves keyboard focus in and out of it.</summary>
public partial class BulkEditView
{
    public BulkEditView() => InitializeComponent();

    /// <summary>Puts keyboard focus on the dialog's first control, as it opens.</summary>
    public void FocusFirst() => CloseButton.Focus();
}
