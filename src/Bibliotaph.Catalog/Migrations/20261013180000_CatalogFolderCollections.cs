using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogFolderCollections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "collection_folder",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                source_root_id = table.Column<long>(type: "INTEGER", nullable: false),
                folder = table.Column<string>(type: "TEXT", nullable: false),
                state = table.Column<string>(type: "TEXT", nullable: false),
                collection_id = table.Column<long>(type: "INTEGER", nullable: true),
                decided_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_collection_folder", x => x.id);
                table.ForeignKey(
                    name: "FK_collection_folder_collection_collection_id",
                    column: x => x.collection_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_collection_folder_source_root_source_root_id",
                    column: x => x.source_root_id,
                    principalTable: "source_root",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_collection_folder_collection_id",
            table: "collection_folder",
            column: "collection_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_collection_folder_source_root_id_folder",
            table: "collection_folder",
            columns: new[] { "source_root_id", "folder" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "collection_folder");
    }
}
