namespace Bibliotaph.App.Views;

/// <summary>Same book, over the Library (multi-select plan). The Library moves keyboard focus in and out of it.</summary>
public partial class SameBookDialogView
{
    public SameBookDialogView() => InitializeComponent();

    /// <summary>Puts keyboard focus on Join as one book, as the dialog opens: the file that opens is already chosen.</summary>
    public void FocusFirst() => JoinButton.Focus();
}
