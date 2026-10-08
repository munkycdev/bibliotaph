using System.Windows.Media;
using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

public sealed partial class NavItemViewModel(Route route, string label, Geometry icon) : ObservableObject
{
    public Route Route { get; } = route;
    public string Label { get; } = label;
    public Geometry Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}
