using System.Windows;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

public partial class LibrarySectionView
{
    public LibrarySectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LibrarySectionViewModel page) page.Storage.PropertyChanged += (_, e) =>
            {
                // The password box can't be bound, so it's emptied when the form closes.
                if (e.PropertyName == nameof(StorageAccountsViewModel.IsWebDavOpen) && !page.Storage.IsWebDavOpen) WebDavPassword.Clear();
            };
        };
    }

    void OnWebDavPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibrarySectionViewModel page) page.Storage.WebDavPassword = WebDavPassword.Password;
    }
}
