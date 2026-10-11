using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class Corrections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "correction_request",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_correction_request", x => x.id);
                table.ForeignKey(
                    name: "FK_correction_request_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "folder_rule",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                source_root_id = table.Column<long>(type: "INTEGER", nullable: false),
                folder = table.Column<string>(type: "TEXT", nullable: false),
                field = table.Column<string>(type: "TEXT", nullable: false),
                value = table.Column<string>(type: "TEXT", nullable: false),
                state = table.Column<string>(type: "TEXT", nullable: false),
                books = table.Column<int>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                decided_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_folder_rule", x => x.id);
                table.ForeignKey(
                    name: "FK_folder_rule_source_root_source_root_id",
                    column: x => x.source_root_id,
                    principalTable: "source_root",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_correction_request_entry_id",
            table: "correction_request",
            column: "entry_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_folder_rule_source_root_id_folder_field_value",
            table: "folder_rule",
            columns: new[] { "source_root_id", "folder", "field", "value" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "correction_request");

        migrationBuilder.DropTable(
            name: "folder_rule");
    }
}
