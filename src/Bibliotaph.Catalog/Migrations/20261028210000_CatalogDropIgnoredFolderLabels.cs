using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogDropIgnoredFolderLabels : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ignored_folder_label");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ignored_folder_label",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                folder = table.Column<string>(type: "TEXT", nullable: false),
                vocabulary = table.Column<string>(type: "TEXT", nullable: false),
                term_key = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ignored_folder_label", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ignored_folder_label_folder_vocabulary_term_key",
            table: "ignored_folder_label",
            columns: new[] { "folder", "vocabulary", "term_key" },
            unique: true);
    }
}
