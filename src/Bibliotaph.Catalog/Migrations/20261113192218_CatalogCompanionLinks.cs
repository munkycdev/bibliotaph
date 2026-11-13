using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class _20261113192218_CatalogCompanionLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "companion_link",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                book_entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                state = table.Column<string>(type: "TEXT", nullable: false),
                evidence = table.Column<string>(type: "TEXT", nullable: true),
                decided_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_companion_link", x => x.id);
                table.ForeignKey(
                    name: "FK_companion_link_entry_book_entry_id",
                    column: x => x.book_entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_companion_link_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_companion_link_book_entry_id",
            table: "companion_link",
            column: "book_entry_id");

        migrationBuilder.CreateIndex(
            name: "IX_companion_link_entry_id_book_entry_id",
            table: "companion_link",
            columns: new[] { "entry_id", "book_entry_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_companion_link_live",
            table: "companion_link",
            column: "entry_id",
            unique: true,
            filter: "state IN ('Suggested', 'Confirmed')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "companion_link");
    }
}
