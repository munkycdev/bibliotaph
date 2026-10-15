using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPartProposalEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "evidence",
            table: "part_proposal",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "evidence_page",
            table: "part_proposal",
            type: "INTEGER",
            nullable: true);

        // Proposals still waiting were made before a book had to say it holds separate adventures, and many were
        // chapters of one campaign. The rules run again over every book and propose those that do; answers stay.
        migrationBuilder.Sql("DELETE FROM part_proposal WHERE answer IS NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "evidence",
            table: "part_proposal");

        migrationBuilder.DropColumn(
            name: "evidence_page",
            table: "part_proposal");
    }
}
