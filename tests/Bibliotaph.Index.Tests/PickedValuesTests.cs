using System.Text.Json;

namespace Bibliotaph.Index.Tests;

/// <summary>The values picked in the type-ahead filters: compared by value, and kept in JSON as a saved search keeps them.</summary>
public sealed class PickedValuesTests
{
    /// <summary>Shaped as a saved search is: a record with defaults, read with the web settings.</summary>
    sealed record SavedView(string Query = "", PickedValues? Picked = null);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static PickedValues? Picks(params (string Field, string[] Values)[] picks) =>
        PickedValues.Of(picks.Select(p => KeyValuePair.Create(p.Field, (IReadOnlyList<string>)p.Values)));

    [Fact]
    public void The_same_picks_in_any_order_are_equal()
    {
        var picked = Picks(("setting", ["the-marches", "sunken-coast"]), ("publisher", ["Kobold Works"]));
        Assert.Equal(picked, Picks(("publisher", ["Kobold Works"]), ("setting", ["sunken-coast", "the-marches", "sunken-coast"])));
        Assert.Equal(picked!.GetHashCode(), Picks(("publisher", ["Kobold Works"]), ("setting", ["sunken-coast", "the-marches"]))!.GetHashCode());
        Assert.NotEqual(picked, Picks(("setting", ["the-marches"]), ("publisher", ["Kobold Works"])));
        Assert.Equal(new SavedView("x", picked), new SavedView("x", Picks(("setting", ["sunken-coast", "the-marches"]), ("publisher", ["Kobold Works"]))));
    }

    [Fact]
    public void No_picks_are_none_and_a_field_can_be_left_out()
    {
        Assert.Null(Picks());
        Assert.Null(Picks(("setting", [])));
        var picked = Picks(("setting", ["the-marches"]), ("theme", ["horror"]))!;
        Assert.Equal(Picks(("theme", ["horror"])), picked.Without("setting"));
        Assert.Null(Picks(("theme", ["horror"]))!.Without("theme"));
        Assert.Equal(["the-marches"], picked["setting"]);
        Assert.Empty(picked["publisher"]);
    }

    [Fact]
    public void A_saved_search_keeps_its_picks_and_an_older_one_has_none()
    {
        var view = new SavedView("dragon", Picks(("setting", ["the-marches", "sunken-coast"]), ("publisher", ["Kobold Works"])));
        var json = JsonSerializer.Serialize(view, Json);
        Assert.Contains("\"picked\":{\"publisher\":[\"Kobold Works\"],\"setting\":[\"the-marches\",\"sunken-coast\"]}", json, StringComparison.Ordinal);
        Assert.Equal(view, JsonSerializer.Deserialize<SavedView>(json, Json));
        Assert.Equal(["the-marches", "sunken-coast"], JsonSerializer.Deserialize<SavedView>(json, Json)!.Picked!["setting"]);

        Assert.Equal(new SavedView("dragon"), JsonSerializer.Deserialize<SavedView>("""{"query":"dragon"}""", Json));
    }
}
