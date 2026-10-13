namespace Bibliotaph.App.Views;

/// <summary>Pack together, over the Library. The Library moves keyboard focus in and out of it.</summary>
public partial class PackTogetherDialogView
{
    public PackTogetherDialogView() => InitializeComponent();

    /// <summary>Puts keyboard focus in the name, with it selected, as the dialog opens.</summary>
    public void FocusFirst()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
