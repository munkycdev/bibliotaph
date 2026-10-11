using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>A screen. The shell shows <see cref="Section"/> / <see cref="Title"/> as the breadcrumb.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract Route Route { get; }

    public abstract string Title { get; }

    public virtual string Section => "Workspace";

    /// <summary>The page <see cref="Section"/> names, which the breadcrumb links to; null when it names no page.</summary>
    public virtual Route? SectionRoute => null;

    /// <summary>
    /// The route whose sidebar item the page marks: its own, except where it sits under another, as a collection's
    /// Library page sits under Collections. Raises PropertyChanged when it changes.
    /// </summary>
    public virtual Route NavRoute => Route;

    /// <summary>
    /// The key of the sidebar pin that opens what the page shows (Pinned Views plan), which marks that pin: a saved
    /// search, a collection, Favorites or a binder. Raises PropertyChanged when it changes.
    /// </summary>
    public virtual string? PinKey => null;

    /// <summary>True for a page with its own scrolling list (a virtualized grid), which the shell must not wrap in a scroll viewer.</summary>
    public virtual bool ScrollsItself => false;

    /// <summary>Called each time the page is shown, including when Back returns to it.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Called when another page replaces this one, to stop listening for updates it no longer shows.</summary>
    public virtual void Unload()
    {
    }

    /// <summary>Called when the pop-out window showing the page closes, to let go of what the page holds.</summary>
    public virtual Task CloseWindowAsync()
    {
        Unload();
        return Task.CompletedTask;
    }
}
