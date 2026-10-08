using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    format = table.Column<string>(type: "TEXT", nullable: false),
                    page_count = table.Column<int>(type: "INTEGER", nullable: true),
                    capabilities_json = table.Column<string>(type: "TEXT", nullable: true),
                    protection = table.Column<string>(type: "TEXT", nullable: false),
                    previous_version_id = table.Column<long>(type: "INTEGER", nullable: true),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_document_previous_version_id",
                        column: x => x.previous_version_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setting", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "source_root",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    volume_serial = table.Column<string>(type: "TEXT", nullable: true),
                    availability = table.Column<string>(type: "TEXT", nullable: false),
                    added_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_root", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assertion",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    document_id = table.Column<long>(type: "INTEGER", nullable: false),
                    field = table.Column<string>(type: "TEXT", nullable: false),
                    value_json = table.Column<string>(type: "TEXT", nullable: false),
                    origin = table.Column<string>(type: "TEXT", nullable: false),
                    evidence_pages_json = table.Column<string>(type: "TEXT", nullable: true),
                    run_id = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assertion", x => x.id);
                    table.ForeignKey(
                        name: "FK_assertion_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_ref",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    document_id = table.Column<long>(type: "INTEGER", nullable: false),
                    first_pdf_page = table.Column<int>(type: "INTEGER", nullable: false),
                    last_pdf_page = table.Column<int>(type: "INTEGER", nullable: false),
                    printed_labels = table.Column<string>(type: "TEXT", nullable: true),
                    text_fingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    is_stale = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_ref", x => x.id);
                    table.ForeignKey(
                        name: "FK_page_ref_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_location",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    source_root_id = table.Column<long>(type: "INTEGER", nullable: false),
                    relative_path = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    modified_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ntfs_file_id = table.Column<string>(type: "TEXT", nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", nullable: true),
                    document_id = table.Column<long>(type: "INTEGER", nullable: true),
                    last_seen_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_location", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_location_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_file_location_source_root_source_root_id",
                        column: x => x.source_root_id,
                        principalTable: "source_root",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assertion_document_id_field_state",
                table: "assertion",
                columns: new[] { "document_id", "field", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_document_content_hash",
                table: "document",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_previous_version_id",
                table: "document",
                column: "previous_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_file_location_content_hash",
                table: "file_location",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_file_location_document_id",
                table: "file_location",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_file_location_source_root_id_relative_path",
                table: "file_location",
                columns: new[] { "source_root_id", "relative_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_ref_document_id",
                table: "page_ref",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_root_path",
                table: "source_root",
                column: "path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assertion");

            migrationBuilder.DropTable(
                name: "file_location");

            migrationBuilder.DropTable(
                name: "page_ref");

            migrationBuilder.DropTable(
                name: "setting");

            migrationBuilder.DropTable(
                name: "source_root");

            migrationBuilder.DropTable(
                name: "document");
        }
    }
}
