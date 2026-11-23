using System.ComponentModel;
using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>Settings &gt; Online libraries: importing the books owned on an online service.</summary>
public partial class OnlineLibrariesView
{
    CatalogueKeysViewModel? _lookups;

    public OnlineLibrariesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    CatalogueKeysViewModel? Lookups => (DataContext as OnlineLibrariesViewModel)?.Lookups;

    /// <summary>A PasswordBox's text can't be bound, so a token or key is handed over as it is typed.</summary>
    void OnRpgGeekTokenChanged(object sender, RoutedEventArgs e)
    {
        if (Lookups is { } lookups) lookups.NewRpgGeekToken = RpgGeekTokenBox.Password;
    }

    void OnGoogleBooksKeyChanged(object sender, RoutedEventArgs e)
    {
        if (Lookups is { } lookups) lookups.NewGoogleBooksKey = GoogleBooksKeyBox.Password;
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_lookups is not null) _lookups.PropertyChanged -= OnLookupsChanged;
        _lookups = Lookups;
        if (_lookups is not null) _lookups.PropertyChanged += OnLookupsChanged;
    }

    /// <summary>Once a token or key is saved, its box empties: a saved one is never shown again.</summary>
    void OnLookupsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CatalogueKeysViewModel lookups) return;
        if (e.PropertyName == nameof(CatalogueKeysViewModel.NewRpgGeekToken) && lookups.NewRpgGeekToken.Length == 0 && RpgGeekTokenBox.Password.Length > 0)
            RpgGeekTokenBox.Clear();
        if (e.PropertyName == nameof(CatalogueKeysViewModel.NewGoogleBooksKey) && lookups.NewGoogleBooksKey.Length == 0 && GoogleBooksKeyBox.Password.Length > 0)
            GoogleBooksKeyBox.Clear();
    }
}
