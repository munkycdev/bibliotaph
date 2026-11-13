using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The details' line for the book on RPGGeek, under its fields: the record it uses, which opens its page there and can
/// be said to be another book's, and "Look up from an RPGGeek link…", a box for the address of the book's page, for a
/// book no lookup found. Only where RPGGeek can be asked, which is while this PC is connected to its web library.
/// </summary>
public sealed record LookupFact(string Label, string Value);

public sealed partial class InspectorViewModel
{
    LookupLinks? _links;

    /// <summary>The RPGGeek record the book uses, found by a lookup or given by its page's address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRpgGeekRecord), nameof(RpgGeekText), nameof(RpgGeekUrl), nameof(RpgGeekOffer), nameof(ShowsRpgGeek))]
    public partial LinkedRecord? RpgGeekRecord { get; private set; }

    /// <summary>The book can be given a record: it has a file of its own, and RPGGeek can be asked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRpgGeek), nameof(OffersRpgGeekLink))]
    public partial bool CanLinkRpgGeek { get; private set; }

    public bool HasRpgGeekRecord => RpgGeekRecord is not null;

    public string RpgGeekText => RpgGeekRecord is { } record ? LookupLinks.Shown(record) : "";

    public string? RpgGeekUrl => RpgGeekRecord?.Url;

    public string RpgGeekOffer => HasRpgGeekRecord ? LookupLinks.OfferAnother : LookupLinks.Offer;

    public bool ShowsRpgGeek => HasRpgGeekRecord || CanLinkRpgGeek;

    /// <summary>The action that opens the box shows until the box does.</summary>
    public bool OffersRpgGeekLink => CanLinkRpgGeek && !IsLinkingRpgGeek;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersRpgGeekLink))]
    public partial bool IsLinkingRpgGeek { get; private set; }

    /// <summary>The address typed or pasted in the box.</summary>
    [ObservableProperty]
    public partial string RpgGeekLink { get; set; } = "";

    /// <summary>Why the address typed wasn't used.</summary>
    [ObservableProperty]
    public partial string? RpgGeekProblem { get; private set; }

    /// <summary>RPGGeek is being asked, one request at a time, which can take some seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpgGeekLookUpLabel))]
    [NotifyCanExecuteChangedFor(nameof(LinkRpgGeekCommand), nameof(NotOnRpgGeekCommand), nameof(CancelRpgGeekLinkCommand))]
    public partial bool IsRpgGeekBusy { get; private set; }

    public string RpgGeekLookUpLabel => IsRpgGeekBusy ? "Looking it up…" : "Look up";

    bool RpgGeekIsFree => !IsRpgGeekBusy;

    /// <summary>What the book's last online lookup searched for and what came of it, a labelled line each.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLookupFacts))]
    public partial IReadOnlyList<LookupFact> LookupFacts { get; private set; } = [];

    public bool HasLookupFacts => LookupFacts.Count > 0;

    async Task ShowRpgGeekAsync()
    {
        if (_links is not { } links) return;
        CanLinkRpgGeek = links.CanLink(Item.Entry);
        RpgGeekRecord = await Task.Run(() => links.GetAsync(Item.Entry));
        LookupFacts = [.. (await Task.Run(() => links.SearchedAsync(Item.Entry))).Select(l => new LookupFact(l.Label, l.Value))];
    }

    [RelayCommand]
    void StartRpgGeekLink()
    {
        RpgGeekLink = "";
        RpgGeekProblem = null;
        IsLinkingRpgGeek = true;
    }

    [RelayCommand(CanExecute = nameof(RpgGeekIsFree))]
    void CancelRpgGeekLink()
    {
        IsLinkingRpgGeek = false;
        RpgGeekLink = "";
        RpgGeekProblem = null;
    }

    /// <summary>Look up: the book takes the record of the item whose page address was pasted, and its fields fill from it.</summary>
    [RelayCommand(CanExecute = nameof(RpgGeekIsFree))]
    async Task LinkRpgGeek()
    {
        if (_links is not { } links) return;
        IsRpgGeekBusy = true;
        RpgGeekProblem = null;
        try
        {
            var link = RpgGeekLink;
            if (await Task.Run(() => links.LinkAsync(Item.Entry, link)) is { } problem)
            {
                RpgGeekProblem = problem;
                return;
            }
            IsLinkingRpgGeek = false;
            RpgGeekLink = "";
            _decided = true;
            await ReloadMetadataAsync(changed: true);
        }
        finally
        {
            IsRpgGeekBusy = false;
        }
    }

    /// <summary>Not this book: the record's suggestions go, and it isn't used for the book again unless its page is given.</summary>
    [RelayCommand(CanExecute = nameof(RpgGeekIsFree))]
    async Task NotOnRpgGeek()
    {
        if (_links is not { } links) return;
        IsRpgGeekBusy = true;
        try
        {
            if (!await Task.Run(() => links.RefuseAsync(Item.Entry))) return;
            _decided = true;
            await ReloadMetadataAsync(changed: true);
        }
        finally
        {
            IsRpgGeekBusy = false;
        }
    }

    /// <summary>Opens the book's page on RPGGeek in the browser.</summary>
    [RelayCommand]
    void OpenRpgGeek()
    {
        if (RpgGeekUrl is { } url && _shell.Open(url) is not null) _prompts.Tell("Windows couldn't open that link.", "Open a web link", PromptKind.Warning);
    }
}
