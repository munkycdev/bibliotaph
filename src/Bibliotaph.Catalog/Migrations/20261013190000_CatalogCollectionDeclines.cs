using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogCollectionDeclines : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "collection_decline",
            columns: table => new
            {
                collection_id = table.Column<long>(type: "INTEGER", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_collection_decline", x => new { x.collection_id, x.entry_id });
                table.ForeignKey(
                    name: "FK_collection_decline_collection_collection_id",
                    column: x => x.collection_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_collection_decline_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_collection_decline_entry_id",
            table: "collection_decline",
            column: "entry_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "collection_decline");
    }
}
