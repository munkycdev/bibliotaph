using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A saved search in the Library's Saved searches list (Pinned Views plan, choice 11): it opens, pins or unpins,
/// renames and deletes through the page it is listed on.
/// </summary>
public sealed partial class SavedSearchRow(SmartViewInfo view, bool isPinned, LibraryViewModel page) : ObservableObject
{
    public SmartViewInfo View { get; } = view;

    public string Name => View.Name;

    public LibraryViewModel Page { get; } = page;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinAction))]
    public partial bool IsPinned { get; set; } = isPinned;

    /// <summary>What its pin button does, as its tooltip and the start of its name.</summary>
    public string PinAction => IsPinned ? "Unpin from the sidebar" : "Pin to the sidebar";
}
