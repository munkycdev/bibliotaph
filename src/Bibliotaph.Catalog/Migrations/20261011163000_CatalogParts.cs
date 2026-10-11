using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogParts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "first_fingerprint",
            table: "entry_source",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "last_fingerprint",
            table: "entry_source",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "first_fingerprint",
            table: "entry_source");

        migrationBuilder.DropColumn(
            name: "last_fingerprint",
            table: "entry_source");
    }
}
