using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogFavorites : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "favorite",
            columns: table => new
            {
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_favorite", x => x.entry_id);
                table.ForeignKey(
                    name: "FK_favorite_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "reading_state",
            columns: table => new
            {
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                page_index = table.Column<int>(type: "INTEGER", nullable: false),
                opened_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reading_state", x => x.entry_id);
                table.ForeignKey(
                    name: "FK_reading_state_document_document_id",
                    column: x => x.document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_reading_state_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_reading_state_document_id",
            table: "reading_state",
            column: "document_id");

        migrationBuilder.CreateIndex(
            name: "IX_reading_state_opened_utc",
            table: "reading_state",
            column: "opened_utc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "favorite");

        migrationBuilder.DropTable(
            name: "reading_state");
    }
}
