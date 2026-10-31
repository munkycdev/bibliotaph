using System.Globalization;
using Bibliotaph.Core;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// How the details pane is laid out in this browser, the same for every book's details: its width, dragged at its left
/// edge, and which sections are folded. Until the user folds or opens one, <see cref="DetailSections.FoldedAtFirst"/>
/// are folded.
/// </summary>
public sealed class DetailsPane(IJSRuntime js)
{
    const string FoldedKey = "details.folded";
    const string WidthKey = "details.width";

    HashSet<string> _folded = [.. DetailSections.FoldedAtFirst];
    bool _loaded;

    /// <summary>The width a drag left the pane at, in CSS pixels; null for the stylesheet's own.</summary>
    public int? Width { get; private set; }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            _folded = [.. DetailSections.Read(await js.InvokeAsync<string?>("bibliotaphPrefs.get", FoldedKey))];
            if (int.TryParse(await js.InvokeAsync<string?>("bibliotaphPrefs.get", WidthKey), NumberStyles.None, CultureInfo.InvariantCulture, out var width))
                Width = width;
        }
        catch (JSException)
        {
            // Nothing remembered: the pane starts as it does the first time.
        }
    }

    public bool IsOpen(string section) => !_folded.Contains(section);

    public Task SetOpenAsync(string section, bool open)
    {
        if (open) _folded.Remove(section);
        else _folded.Add(section);
        return SaveAsync(FoldedKey, DetailSections.Write(_folded));
    }

    public Task SetWidthAsync(int width)
    {
        Width = width;
        return SaveAsync(WidthKey, width.ToString(CultureInfo.InvariantCulture));
    }

    async Task SaveAsync(string key, string value)
    {
        try
        {
            await js.InvokeVoidAsync("bibliotaphPrefs.set", key, value);
        }
        catch (JSException)
        {
            // Kept for this visit only.
        }
    }
}
