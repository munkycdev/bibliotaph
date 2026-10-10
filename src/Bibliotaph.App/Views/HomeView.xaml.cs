using System.Windows.Input;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

public partial class HomeView
{
    public HomeView()
    {
        InitializeComponent();
        AddBookCommand(BookCommands.Open, m => m.OpenBookCommand);
        AddBookCommand(BookCommands.OpenInNewWindow, m => m.OpenBookInNewWindowCommand);
        AddBookCommand(BookCommands.Details, m => m.OpenDetailsCommand);
        AddBookCommand(BookCommands.ToggleFavorite, m => m.ToggleFavoriteCommand);
        CommandBindings.Add(new CommandBinding(BookCommands.AddToCollection,
            (_, e) =>
            {
                if (DataContext is HomeViewModel model && e.Parameter is CollectionRequest request) model.AddToCollection(request);
            },
            (_, e) => e.CanExecute = DataContext is HomeViewModel && e.Parameter is CollectionRequest));
    }

    /// <summary>A cover menu's command, run with the cover's book on Home's own command.</summary>
    void AddBookCommand(RoutedUICommand command, Func<HomeViewModel, ICommand> target) =>
        CommandBindings.Add(new CommandBinding(command,
            (_, e) =>
            {
                if (DataContext is HomeViewModel model && e.Parameter is LibraryItemViewModel item) target(model).Execute(item);
            },
            (_, e) => e.CanExecute = DataContext is HomeViewModel model && e.Parameter is LibraryItemViewModel item && target(model).CanExecute(item)));
}
