using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Catalog.Tests;

/// <summary>RPGGeek's own words for what it files a book under, as RPGGeek writes them, read against the starter vocabulary.</summary>
public sealed class CatalogueTermsTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-catalogue-terms-").FullName;
    CatalogDatabase _database = null!;
    Vocabulary _vocabulary = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateFromTemplateAsync();
        var vocabularies = new VocabularyStore(new Factory(_database));
        await vocabularies.SeedAsync(Ct);
        _vocabulary = await vocabularies.GetAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>"system=dnd edition=dnd-35", or "system:Torg" for a name to propose, in the order they come.</summary>
    string Filed(string field, string value)
    {
        var record = new BookRecord("RPGGeek", "1", "9781234567897", "A Book", [], [], null, [new CatalogueValue(field, value)]);
        return string.Join(' ', BookRecords.Terms([record], _vocabulary).Select(t => t.Term is { } term ? $"{t.Field.Key}={term.Key}" : $"{t.Field.Key}:{t.Label}"));
    }

    [Theory]
    [InlineData("5e Game System Product (D&D 5th Edition Compatible)", "system=dnd edition=dnd-5e")]
    [InlineData("Dungeons & Dragons (3.5 Edition)", "system=dnd edition=dnd-35")]
    [InlineData("d20 System / OGL Product (D&D 3.5 Compatible)", "system=dnd edition=dnd-35")]
    [InlineData("Dungeons & Dragons (Original Edition)", "system=dnd edition=dnd-od")]
    [InlineData("Advanced Dungeons & Dragons (1st Edition)", "system=dnd edition=dnd-1e")]
    [InlineData("AD&D Second Edition (2e) Compatible Products", "system=dnd edition=dnd-2e")]
    [InlineData("Basic Dungeons & Dragons", "system=dnd edition=dnd-basic")]
    [InlineData("Dungeons & Dragons (5th Edition)", "system=dnd edition=dnd-5e")]
    [InlineData("Pathfinder 1E Compatible Product", "system=pathfinder edition=pathfinder-1e")]
    [InlineData("Pathfinder Roleplaying Game (2nd Edition)", "system=pathfinder edition=pathfinder-2e")]
    [InlineData("Call of Cthulhu (2nd - 6th Edition)", "system=call-of-cthulhu")]
    [InlineData("Savage Worlds Adventure Edition (SWADE)", "system=savage-worlds")]
    [InlineData("Traveller: The New Era", "system=traveller")]
    [InlineData("Cepheus Engine System", "system=traveller")]
    [InlineData("13th Age", "system=13th-age")]
    [InlineData("Generic / Universal", "system=system-agnostic")]
    [InlineData("Paranoia (2nd Edition)", "system:Paranoia")]
    public void A_system_is_filed_with_its_edition_or_proposed_by_its_name(string value, string filed) =>
        Assert.Equal(filed, Filed("system", value));

    [Theory]
    [InlineData("Scenario / Adventure / Module", "type=adventure")]
    [InlineData("Core Rules (min needed to play)", "type=rulebook")]
    [InlineData("Alternate Rules (Variant Core Rules)", "type=rulebook")]
    [InlineData("Quick Start / Fast Play Rules", "type=rulebook")]
    [InlineData("Campaign Setting (game world)", "type=setting")]
    [InlineData("Sourcebook (rules/options to enhance play)", "type=supplement")]
    [InlineData("Accessory (dice, maps, screens, cards)", "type=supplement")]
    [InlineData("Solo Play Resource", "type=supplement")]
    // The types are a fixed list, so a category none of them stands for is left out.
    [InlineData("Non-Game Book (but RPG Related)", "")]
    [InlineData("Non-Rankable Item (item will not show in the rankings)", "")]
    public void A_category_is_filed_under_one_of_the_types_or_left_out(string value, string filed) =>
        Assert.Equal(filed, Filed("type", value));

    [Theory]
    [InlineData("Horror (Supernatural)", "theme=horror")]
    [InlineData("Horror (Cthulhu Mythos)", "theme=cosmic-horror theme=horror")]
    [InlineData("Science Fiction (Space Opera)", "theme=sci-fi")]
    [InlineData("Science Fiction (Post Apocalypse)", "theme=survival theme=sci-fi")]
    [InlineData("Action / Adventure (Swashbuckling / Pirates)", "theme=pirates")]
    [InlineData("Comedy / Satire", "theme=comedy")]
    [InlineData("War", "theme=war")]
    [InlineData("History (Medieval)", "theme:History")]
    [InlineData("Weird / Surreal", "theme:Weird / Surreal")]
    // Too wide to tell books apart.
    [InlineData("Fantasy (High Fantasy)", "")]
    [InlineData("Action / Adventure (Pulp)", "")]
    public void A_genre_is_filed_under_a_theme_or_proposed_unless_nearly_every_book_has_it(string value, string filed) =>
        Assert.Equal(filed, Filed("theme", value));

    [Theory]
    [InlineData("The Old World", "setting=the-old-world")]
    [InlineData("The Third Imperium", "setting:The Third Imperium")]
    public void A_setting_is_filed_under_ours_or_proposed(string value, string filed) =>
        Assert.Equal(filed, Filed("setting", value));
}
