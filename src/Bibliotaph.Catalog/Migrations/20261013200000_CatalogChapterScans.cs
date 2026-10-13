using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogChapterScans : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "chapter_scan",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                content_hash = table.Column<string>(type: "TEXT", nullable: false),
                provider = table.Column<string>(type: "TEXT", nullable: false),
                model = table.Column<string>(type: "TEXT", nullable: false),
                prompt_version = table.Column<int>(type: "INTEGER", nullable: false),
                chapters_json = table.Column<string>(type: "TEXT", nullable: false),
                started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_chapter_scan", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_chapter_scan_content_hash",
            table: "chapter_scan",
            column: "content_hash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "chapter_scan");
    }
}
