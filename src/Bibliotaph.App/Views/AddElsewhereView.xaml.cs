namespace Bibliotaph.App.Views;

/// <summary>"Add a book I own elsewhere", over the Library (F5a). The Library moves keyboard focus in and out of it.</summary>
public partial class AddElsewhereView
{
    public AddElsewhereView() => InitializeComponent();

    /// <summary>Puts keyboard focus on the title, as the dialog opens.</summary>
    public void FocusFirst() => TitleBox.Focus();
}
