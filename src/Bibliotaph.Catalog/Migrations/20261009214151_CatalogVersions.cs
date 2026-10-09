using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogVersions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "version_proposal",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                matched_document_id = table.Column<long>(type: "INTEGER", nullable: false),
                evidence = table.Column<string>(type: "TEXT", nullable: false),
                shared_pages = table.Column<int>(type: "INTEGER", nullable: false),
                compared_pages = table.Column<int>(type: "INTEGER", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_version_proposal", x => x.id);
                table.ForeignKey(
                    name: "FK_version_proposal_document_document_id",
                    column: x => x.document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_version_proposal_document_matched_document_id",
                    column: x => x.matched_document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_version_proposal_document_id",
            table: "version_proposal",
            column: "document_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_version_proposal_matched_document_id",
            table: "version_proposal",
            column: "matched_document_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "version_proposal");
    }
}
