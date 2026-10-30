using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogLookupRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "lookup_record",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                content_hash = table.Column<string>(type: "TEXT", nullable: false),
                source = table.Column<string>(type: "TEXT", nullable: false),
                record_key = table.Column<string>(type: "TEXT", nullable: false),
                position = table.Column<int>(type: "INTEGER", nullable: false),
                record_json = table.Column<string>(type: "TEXT", nullable: true),
                printed = table.Column<bool>(type: "INTEGER", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: true),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_lookup_record", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_lookup_record_content_hash_source_record_key",
            table: "lookup_record",
            columns: new[] { "content_hash", "source", "record_key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "lookup_record");
    }
}
