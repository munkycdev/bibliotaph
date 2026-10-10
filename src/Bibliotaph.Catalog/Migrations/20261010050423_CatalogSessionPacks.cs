using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogSessionPacks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Nothing wrote page_ref before session packs, so its columns are reshaped rather than carried over (choice 20).
        migrationBuilder.DropColumn(name: "is_stale", table: "page_ref");
        migrationBuilder.DropColumn(name: "printed_labels", table: "page_ref");
        migrationBuilder.DropColumn(name: "label", table: "page_ref");
        migrationBuilder.RenameColumn(name: "text_fingerprint", table: "page_ref", newName: "first_fingerprint");
        migrationBuilder.AddColumn<string>(name: "last_fingerprint", table: "page_ref", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "first_label", table: "page_ref", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "last_label", table: "page_ref", type: "TEXT", nullable: true);

        migrationBuilder.CreateTable(
            name: "session_pack",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                title = table.Column<string>(type: "TEXT", nullable: false),
                date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                notes = table.Column<string>(type: "TEXT", nullable: true),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                touched_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_pack", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "session_section",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                pack_id = table.Column<long>(type: "INTEGER", nullable: false),
                name = table.Column<string>(type: "TEXT", nullable: false),
                position = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_section", x => x.id);
                table.ForeignKey(
                    name: "FK_session_section_session_pack_pack_id",
                    column: x => x.pack_id,
                    principalTable: "session_pack",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "session_item",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                pack_id = table.Column<long>(type: "INTEGER", nullable: false),
                section_id = table.Column<long>(type: "INTEGER", nullable: true),
                position = table.Column<int>(type: "INTEGER", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                page_ref_id = table.Column<long>(type: "INTEGER", nullable: true),
                label = table.Column<string>(type: "TEXT", nullable: true),
                note = table.Column<string>(type: "TEXT", nullable: true),
                added_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_item", x => x.id);
                table.ForeignKey(
                    name: "FK_session_item_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_session_item_page_ref_page_ref_id",
                    column: x => x.page_ref_id,
                    principalTable: "page_ref",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_session_item_session_pack_pack_id",
                    column: x => x.pack_id,
                    principalTable: "session_pack",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_session_item_session_section_section_id",
                    column: x => x.section_id,
                    principalTable: "session_section",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_session_item_entry_id",
            table: "session_item",
            column: "entry_id");

        migrationBuilder.CreateIndex(
            name: "IX_session_item_pack_id",
            table: "session_item",
            column: "pack_id");

        migrationBuilder.CreateIndex(
            name: "IX_session_item_page_ref_id",
            table: "session_item",
            column: "page_ref_id");

        migrationBuilder.CreateIndex(
            name: "IX_session_item_section_id",
            table: "session_item",
            column: "section_id");

        migrationBuilder.CreateIndex(
            name: "IX_session_pack_touched_utc",
            table: "session_pack",
            column: "touched_utc");

        migrationBuilder.CreateIndex(
            name: "IX_session_section_pack_id",
            table: "session_section",
            column: "pack_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "session_item");

        migrationBuilder.DropTable(
            name: "session_section");

        migrationBuilder.DropTable(
            name: "session_pack");

        migrationBuilder.DropColumn(name: "last_label", table: "page_ref");
        migrationBuilder.DropColumn(name: "first_label", table: "page_ref");
        migrationBuilder.DropColumn(name: "last_fingerprint", table: "page_ref");
        migrationBuilder.RenameColumn(name: "first_fingerprint", table: "page_ref", newName: "text_fingerprint");
        migrationBuilder.AddColumn<string>(name: "label", table: "page_ref", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "printed_labels", table: "page_ref", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "is_stale", table: "page_ref", type: "INTEGER", nullable: false, defaultValue: false);
    }
}
