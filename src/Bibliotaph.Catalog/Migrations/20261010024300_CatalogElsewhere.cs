using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogElsewhere : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "elsewhere_match",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: true),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_elsewhere_match", x => x.id);
                table.ForeignKey(
                    name: "FK_elsewhere_match_document_document_id",
                    column: x => x.document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_elsewhere_match_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_elsewhere_match_document_id",
            table: "elsewhere_match",
            column: "document_id");

        migrationBuilder.CreateIndex(
            name: "IX_elsewhere_match_entry_id_document_id",
            table: "elsewhere_match",
            columns: new[] { "entry_id", "document_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "elsewhere_match");
    }
}
