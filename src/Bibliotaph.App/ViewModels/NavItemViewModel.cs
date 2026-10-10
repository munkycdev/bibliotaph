using System.Windows.Media;
using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

public sealed partial class NavItemViewModel(Route route, string label, Geometry icon, LibraryScope? scope = null, long? viewId = null) : ObservableObject
{
    public Route Route { get; } = route;

    /// <summary>For a Smart Views item such as Favorites: the part of the Library it opens.</summary>
    public LibraryScope? Scope { get; } = scope;

    /// <summary>For a saved Smart View: its id. Its key is in <see cref="ScopeKey"/>.</summary>
    public long? ViewId { get; } = viewId;

    /// <summary>A saved Smart View, which can be renamed or deleted from the sidebar.</summary>
    public bool IsView => ViewId is not null;

    /// <summary>What a page's NavScope reads while this item's page is shown.</summary>
    public string? ScopeKey => ViewId is { } id ? LibraryViewModel.SmartViewKey(id) : Scope?.Key;

    public string Label { get; } = label;
    public Geometry Icon { get; } = icon;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>A count after the label, as Needs review shows how much waits there; empty for none.</summary>
    [ObservableProperty]
    public partial string Count { get; set; } = "";
}
