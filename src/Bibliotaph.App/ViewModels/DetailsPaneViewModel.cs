using System.ComponentModel;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// How the details pane is laid out on this PC, the same for every book's details: its width, dragged at its left edge,
/// and which sections are open. Until the user folds or opens one, <see cref="DetailSections.FoldedAtFirst"/> are folded.
/// </summary>
public sealed partial class DetailsPaneViewModel(SettingsStore store, ILogger<DetailsPaneViewModel> log) : ObservableObject
{
    public const double NarrowestWidth = 440;
    public const double WidestWidth = 960;

    bool _loaded;
    bool _loading;

    [ObservableProperty] public partial double Width { get; set; } = NarrowestWidth;

    [ObservableProperty] public partial bool Adventures { get; set; } = true;
    [ObservableProperty] public partial bool InFile { get; set; } = true;
    [ObservableProperty] public partial bool Pack { get; set; } = true;
    [ObservableProperty] public partial bool Found { get; set; } = true;
    [ObservableProperty] public partial bool Collections { get; set; } = true;
    [ObservableProperty] public partial bool Details { get; set; } = true;
    [ObservableProperty] public partial bool AboutFile { get; set; }
    [ObservableProperty] public partial bool Where { get; set; }
    [ObservableProperty] public partial bool Copies { get; set; }
    [ObservableProperty] public partial bool Indexing { get; set; }

    /// <summary>Reads how the pane was left, the first time any book's details open.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        string? folded, width;
        try
        {
            folded = await store.GetAsync(SettingKeys.DetailsFolded);
            width = await store.GetAsync(SettingKeys.DetailsWidth);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading how the details pane was left failed");
            return;
        }
        var shut = DetailSections.Read(folded);
        _loading = true;
        if (double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide)) Width = Math.Clamp(wide, NarrowestWidth, WidestWidth);
        Adventures = !shut.Contains(DetailSections.Adventures);
        InFile = !shut.Contains(DetailSections.InFile);
        Pack = !shut.Contains(DetailSections.Pack);
        Found = !shut.Contains(DetailSections.Found);
        Collections = !shut.Contains(DetailSections.Collections);
        Details = !shut.Contains(DetailSections.Details);
        AboutFile = !shut.Contains(DetailSections.File);
        Where = !shut.Contains(DetailSections.Where);
        Copies = !shut.Contains(DetailSections.Copies);
        Indexing = !shut.Contains(DetailSections.Indexing);
        _loading = false;
    }

    /// <summary>Keeps the width a drag of the pane's edge ended at.</summary>
    public Task SaveWidthAsync() => SaveAsync(SettingKeys.DetailsWidth, Width.ToString("0", CultureInfo.InvariantCulture));

    IEnumerable<string> Folded()
    {
        if (!Adventures) yield return DetailSections.Adventures;
        if (!InFile) yield return DetailSections.InFile;
        if (!Pack) yield return DetailSections.Pack;
        if (!Found) yield return DetailSections.Found;
        if (!Collections) yield return DetailSections.Collections;
        if (!Details) yield return DetailSections.Details;
        if (!AboutFile) yield return DetailSections.File;
        if (!Where) yield return DetailSections.Where;
        if (!Copies) yield return DetailSections.Copies;
        if (!Indexing) yield return DetailSections.Indexing;
    }

    // A drag changes the width many times a second, so it's kept once the drag ends.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName != nameof(Width)) _ = SaveAsync(SettingKeys.DetailsFolded, DetailSections.Write(Folded()));
    }

    async Task SaveAsync(string key, string value)
    {
        try
        {
            await store.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving how the details pane was left failed");
        }
    }
}
