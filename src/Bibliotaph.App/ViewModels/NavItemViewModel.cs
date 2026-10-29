using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

public sealed partial class NavItemViewModel(Route route, string label, Geometry icon) : ObservableObject
{
    public Route Route { get; } = route;

    public string Label { get; } = label;
    public Geometry Icon { get; } = icon;

    /// <summary>The tour's callout for this page, which its button is the target of.</summary>
    public TourAnchor Anchor { get; } = route switch
    {
        Route.Library => TourAnchor.Library,
        Route.Collections => TourAnchor.Collections,
        Route.Sessions => TourAnchor.Binders,
        Route.NeedsReview => TourAnchor.NeedsReview,
        Route.Settings => TourAnchor.Settings,
        _ => TourAnchor.None,
    };

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>A count after the label, as Needs review shows how much waits there; empty for none.</summary>
    [ObservableProperty]
    public partial string Count { get; set; } = "";
}
