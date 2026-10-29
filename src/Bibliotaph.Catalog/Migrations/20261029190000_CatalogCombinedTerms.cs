using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogCombinedTerms : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "merged_into_key",
            table: "vocabulary_term",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "vocabulary_kept_apart",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                vocabulary = table.Column<string>(type: "TEXT", nullable: false),
                first_key = table.Column<string>(type: "TEXT", nullable: false),
                second_key = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_vocabulary_kept_apart", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_vocabulary_kept_apart_vocabulary_first_key_second_key",
            table: "vocabulary_kept_apart",
            columns: new[] { "vocabulary", "first_key", "second_key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "vocabulary_kept_apart");

        migrationBuilder.DropColumn(
            name: "merged_into_key",
            table: "vocabulary_term");
    }
}
