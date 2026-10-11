using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogAskHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ask_turn",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                asked_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                question = table.Column<string>(type: "TEXT", nullable: false),
                rewritten = table.Column<string>(type: "TEXT", nullable: true),
                answer = table.Column<string>(type: "TEXT", nullable: false),
                parts = table.Column<string>(type: "TEXT", nullable: false),
                cited_pages = table.Column<string>(type: "TEXT", nullable: false),
                model = table.Column<string>(type: "TEXT", nullable: false),
                not_answered = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ask_turn", x => x.id);
                table.ForeignKey(
                    name: "FK_ask_turn_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ask_turn_entry_id",
            table: "ask_turn",
            column: "entry_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ask_turn");
    }
}
