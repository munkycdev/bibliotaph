using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>A screen. The shell shows <see cref="Section"/> / <see cref="Title"/> as the breadcrumb.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract Route Route { get; }

    public abstract string Title { get; }

    public virtual string Section => "Workspace";

    /// <summary>True for a page with its own scrolling list (a virtualized grid), which the shell must not wrap in a scroll viewer.</summary>
    public virtual bool ScrollsItself => false;

    /// <summary>Called each time the page is shown, including when Back returns to it.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Called when another page replaces this one, to stop listening for updates it no longer shows.</summary>
    public virtual void Unload()
    {
    }
}
