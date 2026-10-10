using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPageChecks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "check",
            table: "page_ref",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "check_document_id",
            table: "page_ref",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "check_first_pdf_page",
            table: "page_ref",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "check_last_pdf_page",
            table: "page_ref",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_page_ref_check_document_id",
            table: "page_ref",
            column: "check_document_id");

        migrationBuilder.AddForeignKey(
            name: "FK_page_ref_document_check_document_id",
            table: "page_ref",
            column: "check_document_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_page_ref_document_check_document_id",
            table: "page_ref");

        migrationBuilder.DropIndex(
            name: "IX_page_ref_check_document_id",
            table: "page_ref");

        migrationBuilder.DropColumn(
            name: "check",
            table: "page_ref");

        migrationBuilder.DropColumn(
            name: "check_document_id",
            table: "page_ref");

        migrationBuilder.DropColumn(
            name: "check_first_pdf_page",
            table: "page_ref");

        migrationBuilder.DropColumn(
            name: "check_last_pdf_page",
            table: "page_ref");
    }
}
