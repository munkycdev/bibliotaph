using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogEntries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "entry",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                kind = table.Column<string>(type: "TEXT", nullable: false),
                parent_entry_id = table.Column<long>(type: "INTEGER", nullable: true),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entry", x => x.id);
                table.ForeignKey(
                    name: "FK_entry_entry_parent_entry_id",
                    column: x => x.parent_entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "entry_source",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                first_pdf_page = table.Column<int>(type: "INTEGER", nullable: true),
                last_pdf_page = table.Column<int>(type: "INTEGER", nullable: true),
                is_current = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entry_source", x => x.id);
                table.ForeignKey(
                    name: "FK_entry_source_document_document_id",
                    column: x => x.document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entry_source_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Every document becomes one whole-document entry with the same id, so the user's work moves across 1:1 by
        // renaming its document_id columns (catalog entry design, choices 1 and 2).
        migrationBuilder.Sql("INSERT INTO entry (id, kind, parent_entry_id, created_utc) SELECT id, 'Whole', NULL, created_utc FROM document;");
        migrationBuilder.Sql("INSERT INTO entry_source (entry_id, document_id, first_pdf_page, last_pdf_page, is_current) SELECT id, id, NULL, NULL, 1 FROM document;");

        migrationBuilder.DropForeignKey(
            name: "FK_assertion_document_document_id",
            table: "assertion");

        migrationBuilder.DropForeignKey(
            name: "FK_classification_run_document_document_id",
            table: "classification_run");

        migrationBuilder.DropForeignKey(
            name: "FK_rejection_document_document_id",
            table: "rejection");

        migrationBuilder.RenameColumn(
            name: "document_id",
            table: "rejection",
            newName: "entry_id");

        migrationBuilder.RenameIndex(
            name: "IX_rejection_document_id_field_normalized_value",
            table: "rejection",
            newName: "IX_rejection_entry_id_field_normalized_value");

        migrationBuilder.RenameColumn(
            name: "document_id",
            table: "classification_run",
            newName: "entry_id");

        migrationBuilder.RenameIndex(
            name: "IX_classification_run_document_id",
            table: "classification_run",
            newName: "IX_classification_run_entry_id");

        migrationBuilder.RenameColumn(
            name: "document_id",
            table: "assertion",
            newName: "entry_id");

        migrationBuilder.RenameIndex(
            name: "IX_assertion_document_id_field_state",
            table: "assertion",
            newName: "IX_assertion_entry_id_field_state");

        migrationBuilder.AddColumn<string>(
            name: "content_hash",
            table: "assertion",
            type: "TEXT",
            nullable: true);

        // Every value but the user's own was read from the one copy there was, whose pages its evidence counts in.
        migrationBuilder.Sql("UPDATE assertion SET content_hash = (SELECT d.content_hash FROM document d WHERE d.id = assertion.entry_id) WHERE origin <> 'User';");

        migrationBuilder.CreateIndex(
            name: "IX_entry_parent_entry_id",
            table: "entry",
            column: "parent_entry_id");

        migrationBuilder.CreateIndex(
            name: "ix_entry_source_current",
            table: "entry_source",
            column: "entry_id",
            unique: true,
            filter: "is_current = 1");

        migrationBuilder.CreateIndex(
            name: "IX_entry_source_document_id_entry_id",
            table: "entry_source",
            columns: new[] { "document_id", "entry_id" });

        migrationBuilder.CreateIndex(
            name: "ix_entry_source_whole",
            table: "entry_source",
            column: "document_id",
            unique: true,
            filter: "first_pdf_page IS NULL");

        migrationBuilder.AddForeignKey(
            name: "FK_assertion_entry_entry_id",
            table: "assertion",
            column: "entry_id",
            principalTable: "entry",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_classification_run_entry_entry_id",
            table: "classification_run",
            column: "entry_id",
            principalTable: "entry",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_rejection_entry_entry_id",
            table: "rejection",
            column: "entry_id",
            principalTable: "entry",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_assertion_entry_entry_id",
            table: "assertion");

        migrationBuilder.DropForeignKey(
            name: "FK_classification_run_entry_entry_id",
            table: "classification_run");

        migrationBuilder.DropForeignKey(
            name: "FK_rejection_entry_entry_id",
            table: "rejection");

        migrationBuilder.DropTable(
            name: "entry_source");

        migrationBuilder.DropTable(
            name: "entry");

        migrationBuilder.DropColumn(
            name: "content_hash",
            table: "assertion");

        migrationBuilder.RenameColumn(
            name: "entry_id",
            table: "rejection",
            newName: "document_id");

        migrationBuilder.RenameIndex(
            name: "IX_rejection_entry_id_field_normalized_value",
            table: "rejection",
            newName: "IX_rejection_document_id_field_normalized_value");

        migrationBuilder.RenameColumn(
            name: "entry_id",
            table: "classification_run",
            newName: "document_id");

        migrationBuilder.RenameIndex(
            name: "IX_classification_run_entry_id",
            table: "classification_run",
            newName: "IX_classification_run_document_id");

        migrationBuilder.RenameColumn(
            name: "entry_id",
            table: "assertion",
            newName: "document_id");

        migrationBuilder.RenameIndex(
            name: "IX_assertion_entry_id_field_state",
            table: "assertion",
            newName: "IX_assertion_document_id_field_state");

        migrationBuilder.AddForeignKey(
            name: "FK_assertion_document_document_id",
            table: "assertion",
            column: "document_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_classification_run_document_document_id",
            table: "classification_run",
            column: "document_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_rejection_document_document_id",
            table: "rejection",
            column: "document_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
    }
}
