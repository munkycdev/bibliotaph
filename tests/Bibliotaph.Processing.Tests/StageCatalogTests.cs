using System.Runtime.CompilerServices;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

/// <summary>What each stage says about itself, and the catalog the lanes, startup and the app read it from.</summary>
public sealed class StageCatalogTests
{
    sealed record Facts(Stage Stage, int Version, Lane Lane, Stage? After, bool Backfill, string Label, string Step, string Running);

    /// <summary>Versions, lanes, what queues each stage, what startup backfills and what the app calls each stage.</summary>
    static readonly Facts[] Expected =
    [
        new(Stage.Probe, 1, Lane.Index, null, false, "Opening", "Opening", "Opening"),
        new(Stage.Text, 1, Lane.Index, Stage.Probe, false, "Reading text", "Reading text", "Reading the text of"),
        new(Stage.Covers, 1, Lane.Index, Stage.Probe, false, "Cover", "Making a cover", "Making a cover for"),
        new(Stage.RuleHints, 2, Lane.Index, Stage.Probe, true, "Hints from names", "Reading hints from its name", "Working on"),
        new(Stage.Ocr, 1, Lane.Ocr, null, false, "Reading scanned pages", "Reading scanned pages", "Reading scanned pages of"),
        new(Stage.Classify, 1, Lane.Classify, Stage.Text, true, "Cataloguing with AI", "Reading with AI", "Asking the AI about"),
        new(Stage.Match, 2, Lane.Index, Stage.Text, true, "Looking for other copies", "Looking for other copies", "Working on"),
        new(Stage.ChapterScan, 1, Lane.Classify, Stage.Text, true, "Scanning the whole book", "Scanning the whole book", "Scanning the chapters of"),
    ];

    /// <summary>
    /// Every stage in Processing, made without its services: what a stage says about itself never needs them, and
    /// finding them this way means a new stage can't be left out.
    /// </summary>
    static StageCatalog Catalog() => new(typeof(IStage).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IStage)))
        .Select(t => (IStage)RuntimeHelpers.GetUninitializedObject(t)));

    [Fact]
    public void Every_step_after_hashing_has_exactly_one_stage()
    {
        Assert.Equal(Enum.GetValues<Stage>().Where(s => s != Stage.Fingerprint), Catalog().All.Select(s => s.Stage));
    }

    [Fact]
    public void Each_stage_keeps_its_version_lane_backfill_and_names()
    {
        Assert.Equal(Expected, Catalog().All.Select(s =>
            new Facts(s.Stage, Pipeline.Version(s.Stage), s.Lane, s.After, s.Backfill, s.Names.Label, s.Names.Step, s.Names.Running)));
    }

    [Fact]
    public void Probe_is_followed_by_text_covers_and_hints_and_text_by_the_rest_but_ocr()
    {
        var catalog = Catalog();

        Assert.Equal([Stage.Text, Stage.Covers, Stage.RuleHints], catalog.Following(Stage.Probe));
        // Text asks for OCR itself, only when pages need it.
        Assert.Equal([Stage.Classify, Stage.Match, Stage.ChapterScan], catalog.Following(Stage.Text));
        Assert.All(Enum.GetValues<Stage>().Except([Stage.Probe, Stage.Text]), s => Assert.Empty(catalog.Following(s)));
    }

    [Fact]
    public void Two_stages_for_one_step_are_refused() =>
        Assert.Throws<ArgumentException>(() => new StageCatalog([new ProbeStage(null!), new ProbeStage(null!)]));

    [Fact]
    public void A_step_with_no_stage_is_not_found_and_leads_nowhere()
    {
        var catalog = new StageCatalog([new ProbeStage(null!)]);

        Assert.Null(catalog.Find(Stage.Text));
        Assert.Empty(catalog.Following(Stage.Probe));
    }
}
