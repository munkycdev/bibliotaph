using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogBinderAtHand : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "game_system",
            table: "session_pack",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "keep_at_hand",
            table: "session_pack",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "game_system",
            table: "session_pack");

        migrationBuilder.DropColumn(
            name: "keep_at_hand",
            table: "session_pack");
    }
}
