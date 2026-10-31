using Bibliotaph.Core;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// Which sections of a book's details are folded, the same for every book's details and remembered in this browser.
/// Until the user folds or opens one, <see cref="DetailSections.FoldedAtFirst"/> are folded.
/// </summary>
public sealed class DetailSectionState(IJSRuntime js)
{
    const string Key = "details.folded";

    HashSet<string> _folded = [.. DetailSections.FoldedAtFirst];
    bool _loaded;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            _folded = [.. DetailSections.Read(await js.InvokeAsync<string?>("bibliotaphPrefs.get", Key))];
        }
        catch (JSException)
        {
            // Nothing remembered: the sections start as they do the first time.
        }
    }

    public bool IsOpen(string section) => !_folded.Contains(section);

    public async Task SetOpenAsync(string section, bool open)
    {
        if (open) _folded.Remove(section);
        else _folded.Add(section);
        try
        {
            await js.InvokeVoidAsync("bibliotaphPrefs.set", Key, DetailSections.Write(_folded));
        }
        catch (JSException)
        {
            // Kept for this visit only.
        }
    }
}
