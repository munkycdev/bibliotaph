using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>Every saved Smart View, listed by name, and the changes the Library makes to them.</summary>
public interface ISmartViewDirectory
{
    /// <summary>Raised on the UI thread after the views were reloaded.</summary>
    event EventHandler? Changed;

    IReadOnlyList<SmartViewInfo> All { get; }

    SmartViewInfo? Find(long viewId);

    Task<SmartViewInfo> CreateAsync(string name, SmartViewDefinition definition);

    Task<bool> UpdateAsync(long viewId, SmartViewDefinition definition);

    Task<bool> RenameAsync(long viewId, string name);

    Task<SmartViewDeletion?> DeleteAsync(long viewId);

    Task<SmartViewInfo> RestoreAsync(SmartViewDeletion deleted);
}

/// <summary>
/// Every saved Smart View, kept in memory for the sidebar, Home and the Library, listed by name. Changes go through
/// here, and <see cref="Changed"/> is raised on the UI thread after each.
/// </summary>
public sealed class SmartViewDirectory(SmartViewStore store, ILogger<SmartViewDirectory> log) : ISmartViewDirectory
{
    /// <summary>Raised on the UI thread after the views were reloaded.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SmartViewInfo> All { get; private set; } = [];

    public SmartViewInfo? Find(long viewId) => All.FirstOrDefault(v => v.Id == viewId);

    public async Task LoadAsync()
    {
        try
        {
            All = await Task.Run(() => store.ListAsync());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the Smart Views failed");
        }
    }

    public Task<SmartViewInfo> CreateAsync(string name, SmartViewDefinition definition) => ThenLoad(() => store.CreateAsync(name, definition.ToJson()));

    public Task<bool> UpdateAsync(long viewId, SmartViewDefinition definition) => ThenLoad(() => store.UpdateAsync(viewId, definition.ToJson()));

    public Task<bool> RenameAsync(long viewId, string name) => ThenLoad(() => store.RenameAsync(viewId, name));

    public Task<SmartViewDeletion?> DeleteAsync(long viewId) => ThenLoad(() => store.DeleteAsync(viewId));

    public Task<SmartViewInfo> RestoreAsync(SmartViewDeletion deleted) => ThenLoad(() => store.RestoreAsync(deleted));

    async Task<T> ThenLoad<T>(Func<Task<T>> change)
    {
        var result = await Task.Run(change);
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(LoadAsync).Task.Unwrap();
        else await LoadAsync();
        return result;
    }
}
