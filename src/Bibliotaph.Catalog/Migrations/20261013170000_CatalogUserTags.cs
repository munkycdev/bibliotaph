using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogUserTags : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "user_tag",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                name = table.Column<string>(type: "TEXT", nullable: false),
                normalized_name = table.Column<string>(type: "TEXT", nullable: false),
                words_json = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_tag", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_user_tag_normalized_name",
            table: "user_tag",
            column: "normalized_name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "user_tag");
    }
}
