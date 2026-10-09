using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

public partial class AiSettingsView : UserControl
{
    public AiSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>A PasswordBox's text can't be bound, so the key is handed over as it is typed.</summary>
    void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AiSettingsViewModel page) page.NewKey = KeyBox.Password;
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AiSettingsViewModel old)
        {
            old.PropertyChanged -= OnPagePropertyChanged;
            old.FocusEndpointRequested -= OnFocusEndpointRequested;
        }
        if (e.NewValue is AiSettingsViewModel page)
        {
            page.PropertyChanged += OnPagePropertyChanged;
            page.FocusEndpointRequested += OnFocusEndpointRequested;
        }
    }

    /// <summary>Setting up starts at the address: after "On" was refused, the cursor goes there.</summary>
    void OnFocusEndpointRequested(object? sender, EventArgs e)
    {
        EndpointBox.Focus();
        EndpointBox.SelectAll();
    }

    /// <summary>Once a key is saved, the box empties: the saved key is never shown again.</summary>
    void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiSettingsViewModel.NewKey) && sender is AiSettingsViewModel { NewKey.Length: 0 } && KeyBox.Password.Length > 0)
            KeyBox.Clear();
    }
}
