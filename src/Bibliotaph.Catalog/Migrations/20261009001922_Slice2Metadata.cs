using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class Slice2Metadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "decided_utc",
                table: "assertion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_quote",
                table: "assertion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "from_sampling",
                table: "assertion",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "normalized_value",
                table: "assertion",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "classification_run",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    document_id = table.Column<long>(type: "INTEGER", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    prompt_version = table.Column<int>(type: "INTEGER", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    pages_json = table.Column<string>(type: "TEXT", nullable: true),
                    started_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classification_run", x => x.id);
                    table.ForeignKey(
                        name: "FK_classification_run_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ignored_folder_label",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    folder = table.Column<string>(type: "TEXT", nullable: false),
                    vocabulary = table.Column<string>(type: "TEXT", nullable: false),
                    term_key = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ignored_folder_label", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rejection",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    document_id = table.Column<long>(type: "INTEGER", nullable: false),
                    field = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_value = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rejection", x => x.id);
                    table.ForeignKey(
                        name: "FK_rejection_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_term",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    vocabulary = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    short_label = table.Column<string>(type: "TEXT", nullable: true),
                    parent_key = table.Column<string>(type: "TEXT", nullable: true),
                    origin = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_term", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_alias",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    term_id = table.Column<long>(type: "INTEGER", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: false),
                    normalized = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_alias", x => x.id);
                    table.ForeignKey(
                        name: "FK_vocabulary_alias_vocabulary_term_term_id",
                        column: x => x.term_id,
                        principalTable: "vocabulary_term",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_classification_run_content_hash_model_prompt_version",
                table: "classification_run",
                columns: new[] { "content_hash", "model", "prompt_version" });

            migrationBuilder.CreateIndex(
                name: "IX_classification_run_document_id",
                table: "classification_run",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_ignored_folder_label_folder_vocabulary_term_key",
                table: "ignored_folder_label",
                columns: new[] { "folder", "vocabulary", "term_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rejection_document_id_field_normalized_value",
                table: "rejection",
                columns: new[] { "document_id", "field", "normalized_value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_alias_term_id_normalized",
                table: "vocabulary_alias",
                columns: new[] { "term_id", "normalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_term_vocabulary_key",
                table: "vocabulary_term",
                columns: new[] { "vocabulary", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "classification_run");

            migrationBuilder.DropTable(
                name: "ignored_folder_label");

            migrationBuilder.DropTable(
                name: "rejection");

            migrationBuilder.DropTable(
                name: "vocabulary_alias");

            migrationBuilder.DropTable(
                name: "vocabulary_term");

            migrationBuilder.DropColumn(
                name: "decided_utc",
                table: "assertion");

            migrationBuilder.DropColumn(
                name: "evidence_quote",
                table: "assertion");

            migrationBuilder.DropColumn(
                name: "from_sampling",
                table: "assertion");

            migrationBuilder.DropColumn(
                name: "normalized_value",
                table: "assertion");
        }
    }
}
