using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogCollections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "collection",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                parent_id = table.Column<long>(type: "INTEGER", nullable: true),
                name = table.Column<string>(type: "TEXT", nullable: false),
                description = table.Column<string>(type: "TEXT", nullable: true),
                pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                used_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_collection", x => x.id);
                table.ForeignKey(
                    name: "FK_collection_collection_parent_id",
                    column: x => x.parent_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "collection_item",
            columns: table => new
            {
                collection_id = table.Column<long>(type: "INTEGER", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                added_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_collection_item", x => new { x.collection_id, x.entry_id });
                table.ForeignKey(
                    name: "FK_collection_item_collection_collection_id",
                    column: x => x.collection_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_collection_item_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_collection_parent_id",
            table: "collection",
            column: "parent_id");

        migrationBuilder.CreateIndex(
            name: "IX_collection_item_entry_id",
            table: "collection_item",
            column: "entry_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "collection_item");

        migrationBuilder.DropTable(
            name: "collection");
    }
}
