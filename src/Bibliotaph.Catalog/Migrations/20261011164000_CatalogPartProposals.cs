using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPartProposals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "part_proposal",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                content_hash = table.Column<string>(type: "TEXT", nullable: false),
                source = table.Column<string>(type: "TEXT", nullable: false),
                parts_json = table.Column<string>(type: "TEXT", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: true),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                decided_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_part_proposal", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_part_proposal_content_hash",
            table: "part_proposal",
            column: "content_hash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "part_proposal");
    }
}
