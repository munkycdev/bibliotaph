using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogCopies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_document_document_previous_version_id",
            table: "document");

        migrationBuilder.DropIndex(
            name: "IX_document_previous_version_id",
            table: "document");

        migrationBuilder.DropColumn(
            name: "previous_version_id",
            table: "document");

        migrationBuilder.AddColumn<long>(
            name: "previous_document_id",
            table: "file_location",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "merged_into_entry_id",
            table: "entry",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "copy_decision",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                first_hash = table.Column<string>(type: "TEXT", nullable: false),
                second_hash = table.Column<string>(type: "TEXT", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_copy_decision", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "entry_join",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                joined_entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                matched_document_id = table.Column<long>(type: "INTEGER", nullable: false),
                moved_json = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entry_join", x => x.id);
                table.ForeignKey(
                    name: "FK_entry_join_document_document_id",
                    column: x => x.document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entry_join_document_matched_document_id",
                    column: x => x.matched_document_id,
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entry_join_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entry_join_entry_joined_entry_id",
                    column: x => x.joined_entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_file_location_previous_document_id",
            table: "file_location",
            column: "previous_document_id");

        migrationBuilder.CreateIndex(
            name: "IX_entry_merged_into_entry_id",
            table: "entry",
            column: "merged_into_entry_id");

        migrationBuilder.CreateIndex(
            name: "IX_copy_decision_first_hash_second_hash",
            table: "copy_decision",
            columns: new[] { "first_hash", "second_hash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_entry_join_document_id",
            table: "entry_join",
            column: "document_id");

        migrationBuilder.CreateIndex(
            name: "IX_entry_join_entry_id",
            table: "entry_join",
            column: "entry_id");

        migrationBuilder.CreateIndex(
            name: "IX_entry_join_joined_entry_id",
            table: "entry_join",
            column: "joined_entry_id");

        migrationBuilder.CreateIndex(
            name: "IX_entry_join_matched_document_id",
            table: "entry_join",
            column: "matched_document_id");

        migrationBuilder.AddForeignKey(
            name: "FK_entry_entry_merged_into_entry_id",
            table: "entry",
            column: "merged_into_entry_id",
            principalTable: "entry",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_file_location_document_previous_document_id",
            table: "file_location",
            column: "previous_document_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_entry_entry_merged_into_entry_id",
            table: "entry");

        migrationBuilder.DropForeignKey(
            name: "FK_file_location_document_previous_document_id",
            table: "file_location");

        migrationBuilder.DropTable(
            name: "copy_decision");

        migrationBuilder.DropTable(
            name: "entry_join");

        migrationBuilder.DropIndex(
            name: "IX_file_location_previous_document_id",
            table: "file_location");

        migrationBuilder.DropIndex(
            name: "IX_entry_merged_into_entry_id",
            table: "entry");

        migrationBuilder.DropColumn(
            name: "previous_document_id",
            table: "file_location");

        migrationBuilder.DropColumn(
            name: "merged_into_entry_id",
            table: "entry");

        migrationBuilder.AddColumn<long>(
            name: "previous_version_id",
            table: "document",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_document_previous_version_id",
            table: "document",
            column: "previous_version_id");

        migrationBuilder.AddForeignKey(
            name: "FK_document_document_previous_version_id",
            table: "document",
            column: "previous_version_id",
            principalTable: "document",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }
}
