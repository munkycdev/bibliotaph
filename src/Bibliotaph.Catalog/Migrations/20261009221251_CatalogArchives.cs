using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogArchives : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "container_id",
            table: "file_location",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "entry_crc32",
            table: "file_location",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "entry_path",
            table: "file_location",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "problem",
            table: "file_location",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_file_location_container_id",
            table: "file_location",
            column: "container_id");

        migrationBuilder.AddForeignKey(
            name: "FK_file_location_file_location_container_id",
            table: "file_location",
            column: "container_id",
            principalTable: "file_location",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_file_location_file_location_container_id",
            table: "file_location");

        migrationBuilder.DropIndex(
            name: "IX_file_location_container_id",
            table: "file_location");

        migrationBuilder.DropColumn(
            name: "container_id",
            table: "file_location");

        migrationBuilder.DropColumn(
            name: "entry_crc32",
            table: "file_location");

        migrationBuilder.DropColumn(
            name: "entry_path",
            table: "file_location");

        migrationBuilder.DropColumn(
            name: "problem",
            table: "file_location");
    }
}
