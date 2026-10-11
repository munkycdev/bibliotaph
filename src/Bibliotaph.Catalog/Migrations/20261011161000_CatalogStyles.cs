using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <summary>
/// Style splits off Themes (scenario review, row 6): the starter themes that name a kind of adventure (heist, dungeon
/// crawl, exploration) become style terms with their keys, labels and aliases, and every value, rejection and switched-off
/// folder label of theirs moves with them. Saved searches that asked for them by key ask for the style instead. Aliases
/// that now belong to new starter terms leave the old ones: hexcrawl and pointcrawl leave exploration, jungle leaves
/// forest, investigation leaves mystery. The new terms themselves arrive with starter vocabulary version 3.
/// </summary>
public partial class CatalogStyles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE vocabulary_term SET vocabulary = 'style' WHERE vocabulary = 'theme' AND key IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE assertion SET field = 'style' WHERE field = 'theme' AND normalized_value IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE rejection SET field = 'style' WHERE field = 'theme' AND normalized_value IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE ignored_folder_label SET vocabulary = 'style' WHERE vocabulary = 'theme' AND term_key IN ('heist', 'dungeon-crawl', 'exploration');
            DELETE FROM vocabulary_alias WHERE normalized IN ('hexcrawl', 'hex crawl', 'pointcrawl', 'point crawl')
                AND term_id IN (SELECT id FROM vocabulary_term WHERE vocabulary = 'style' AND key = 'exploration');
            DELETE FROM vocabulary_alias WHERE normalized IN ('jungle', 'jungles')
                AND term_id IN (SELECT id FROM vocabulary_term WHERE vocabulary = 'environment' AND key = 'forest');
            DELETE FROM vocabulary_alias WHERE normalized IN ('investigation', 'investigative')
                AND term_id IN (SELECT id FROM vocabulary_term WHERE vocabulary = 'theme' AND key = 'mystery');
            UPDATE smart_view SET definition =
                replace(replace(replace(replace(definition, 'theme:heist', 'style:heist'), 'theme:dungeon', 'style:dungeon'),
                    'theme:explor', 'style:explor'), 'theme:\u0022dungeon', 'style:\u0022dungeon');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE vocabulary_term SET vocabulary = 'theme' WHERE vocabulary = 'style' AND key IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE assertion SET field = 'theme' WHERE field = 'style' AND normalized_value IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE rejection SET field = 'theme' WHERE field = 'style' AND normalized_value IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE ignored_folder_label SET vocabulary = 'theme' WHERE vocabulary = 'style' AND term_key IN ('heist', 'dungeon-crawl', 'exploration');
            UPDATE smart_view SET definition =
                replace(replace(replace(replace(definition, 'style:heist', 'theme:heist'), 'style:dungeon', 'theme:dungeon'),
                    'style:explor', 'theme:explor'), 'style:\u0022dungeon', 'theme:\u0022dungeon');
            """);
    }
}
