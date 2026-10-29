using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogAiOverlaps : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "vocabulary_ai_overlap",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                vocabulary = table.Column<string>(type: "TEXT", nullable: false),
                keys = table.Column<string>(type: "TEXT", nullable: false),
                reason = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_vocabulary_ai_overlap", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_vocabulary_ai_overlap_vocabulary_keys",
            table: "vocabulary_ai_overlap",
            columns: new[] { "vocabulary", "keys" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "vocabulary_ai_overlap");
    }
}
