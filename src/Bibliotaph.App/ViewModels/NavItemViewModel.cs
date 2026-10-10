using System.Windows.Media;
using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

public sealed partial class NavItemViewModel(Route route, string label, Geometry icon, LibraryScope? scope = null) : ObservableObject
{
    public Route Route { get; } = route;

    /// <summary>For a Smart Views item such as Favorites: the part of the Library it opens.</summary>
    public LibraryScope? Scope { get; } = scope;
    public string Label { get; } = label;
    public Geometry Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>A count after the label, as Needs review shows how much waits there; empty for none.</summary>
    [ObservableProperty]
    public partial string Count { get; set; } = "";
}
