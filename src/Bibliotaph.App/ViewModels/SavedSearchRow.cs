using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A saved search in the Library's Saved searches list: it opens, pins or unpins, renames and deletes through the
/// list it is in.
/// </summary>
public sealed partial class SavedSearchRow(SmartViewInfo view, bool isPinned, SmartViewsViewModel searches) : ObservableObject
{
    public SmartViewInfo View { get; } = view;

    public string Name => View.Name;

    public SmartViewsViewModel Searches { get; } = searches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinAction))]
    public partial bool IsPinned { get; set; } = isPinned;

    /// <summary>What its pin button does, as its tooltip and the start of its name.</summary>
    public string PinAction => IsPinned ? "Unpin from the sidebar" : "Pin to the sidebar";
}
