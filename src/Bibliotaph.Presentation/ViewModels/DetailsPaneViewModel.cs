using System.ComponentModel;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.ViewModels;

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

    [ObservableProperty] public partial bool Glance { get; set; } = true;
    [ObservableProperty] public partial bool Adventures { get; set; } = true;
    [ObservableProperty] public partial bool InFile { get; set; } = true;
    [ObservableProperty] public partial bool GoesWith { get; set; } = true;
    [ObservableProperty] public partial bool Pack { get; set; } = true;
    [ObservableProperty] public partial bool Inside { get; set; } = true;
    [ObservableProperty] public partial bool Collections { get; set; } = true;
    [ObservableProperty] public partial bool Publication { get; set; }
    [ObservableProperty] public partial bool File { get; set; }
    [ObservableProperty] public partial bool Copies { get; set; }
    [ObservableProperty] public partial bool Read { get; set; }

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
        var shut = DetailSections.ReadFolded(folded);
        _loading = true;
        if (double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide)) Width = Math.Clamp(wide, NarrowestWidth, WidestWidth);
        Glance = !shut.Contains(DetailSections.Glance);
        Adventures = !shut.Contains(DetailSections.Adventures);
        InFile = !shut.Contains(DetailSections.InFile);
        GoesWith = !shut.Contains(DetailSections.GoesWith);
        Pack = !shut.Contains(DetailSections.Pack);
        Inside = !shut.Contains(DetailSections.Inside);
        Collections = !shut.Contains(DetailSections.Collections);
        Publication = !shut.Contains(DetailSections.Publication);
        File = !shut.Contains(DetailSections.File);
        Copies = !shut.Contains(DetailSections.Copies);
        Read = !shut.Contains(DetailSections.Read);
        _loading = false;
    }

    /// <summary>Keeps the width a drag of the pane's edge ended at.</summary>
    public Task SaveWidthAsync() => SaveAsync(SettingKeys.DetailsWidth, Width.ToString("0", CultureInfo.InvariantCulture));

    IEnumerable<string> Folded()
    {
        if (!Glance) yield return DetailSections.Glance;
        if (!Adventures) yield return DetailSections.Adventures;
        if (!InFile) yield return DetailSections.InFile;
        if (!GoesWith) yield return DetailSections.GoesWith;
        if (!Pack) yield return DetailSections.Pack;
        if (!Inside) yield return DetailSections.Inside;
        if (!Collections) yield return DetailSections.Collections;
        if (!Publication) yield return DetailSections.Publication;
        if (!File) yield return DetailSections.File;
        if (!Copies) yield return DetailSections.Copies;
        if (!Read) yield return DetailSections.Read;
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
