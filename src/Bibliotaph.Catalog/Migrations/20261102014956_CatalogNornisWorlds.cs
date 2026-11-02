using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class _20261102014956_CatalogNornisWorlds : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nornis_world",
            columns: table => new
            {
                world_id = table.Column<string>(type: "TEXT", nullable: false),
                collection_id = table.Column<long>(type: "INTEGER", nullable: true),
                name = table.Column<string>(type: "TEXT", nullable: false),
                name_managed = table.Column<bool>(type: "INTEGER", nullable: false),
                gone_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                last_occurred_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nornis_world", x => x.world_id);
                table.ForeignKey(
                    name: "FK_nornis_world_collection_collection_id",
                    column: x => x.collection_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nornis_world_collection_id",
            table: "nornis_world",
            column: "collection_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "nornis_world");
    }
}
