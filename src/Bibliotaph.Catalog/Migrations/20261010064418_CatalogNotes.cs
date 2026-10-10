using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogNotes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "note",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                page_ref_id = table.Column<long>(type: "INTEGER", nullable: true),
                text = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                updated_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_note", x => x.id);
                table.ForeignKey(
                    name: "FK_note_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_note_page_ref_page_ref_id",
                    column: x => x.page_ref_id,
                    principalTable: "page_ref",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_note_entry_id_page_ref_id",
            table: "note",
            columns: new[] { "entry_id", "page_ref_id" });

        migrationBuilder.CreateIndex(
            name: "ix_note_entry_own",
            table: "note",
            column: "entry_id",
            unique: true,
            filter: "page_ref_id IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_note_page_ref_id",
            table: "note",
            column: "page_ref_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "note");
    }
}
