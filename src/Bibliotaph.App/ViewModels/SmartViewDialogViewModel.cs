using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>Naming a Smart View (slice 3 plan, choice 17): Save view, Save as new and Rename.</summary>
public sealed partial class SmartViewDialogViewModel(string heading, string actionText, string name = "") : ObservableObject
{
    /// <summary>Raised when the dialog closes: with the name, or null when it was cancelled.</summary>
    public event EventHandler<string?>? Closed;

    public string Heading { get; } = heading;

    public string ActionText { get; } = actionText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = name;

    bool CanSave => Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save() => Closed?.Invoke(this, Name.Trim());

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}
