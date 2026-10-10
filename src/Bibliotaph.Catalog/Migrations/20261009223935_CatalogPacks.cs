using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPacks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pack_decision",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                source_root_id = table.Column<long>(type: "INTEGER", nullable: false),
                folder_path = table.Column<string>(type: "TEXT", nullable: false),
                is_archive = table.Column<bool>(type: "INTEGER", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pack_decision", x => x.id);
                table.ForeignKey(
                    name: "FK_pack_decision_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_pack_decision_source_root_source_root_id",
                    column: x => x.source_root_id,
                    principalTable: "source_root",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_pack_decision_entry_id",
            table: "pack_decision",
            column: "entry_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_pack_decision_source_root_id_folder_path",
            table: "pack_decision",
            columns: new[] { "source_root_id", "folder_path" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "pack_decision");
    }
}
