using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
/// <summary>
/// Library sync (library sync plan, choice 4): a sync id on each kind of row the user makes, given to the rows already
/// there before its unique index goes on, and the sync_state table that remembers what this PC last saw of each record.
/// </summary>
public partial class CatalogLibrarySync : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "smart_view",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "session_section",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "session_pack",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "session_item",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "note",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "entry",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "sync_id",
            table: "collection",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateTable(
            name: "sync_state",
            columns: table => new
            {
                key = table.Column<string>(type: "TEXT", nullable: false),
                fields_json = table.Column<string>(type: "TEXT", nullable: false),
                aliases_json = table.Column<string>(type: "TEXT", nullable: true),
                deleted_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                pending = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sync_state", x => x.key);
            });

        // Every existing row gets its own random id, in the uppercase form EF Core stores a Guid in.
        foreach (var table in new[] { "smart_view", "session_section", "session_pack", "session_item", "note", "entry", "collection" })
            migrationBuilder.Sql($"""
                UPDATE "{table}" SET sync_id = hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-'
                    || hex(randomblob(2)) || '-' || hex(randomblob(6));
                """);

        migrationBuilder.CreateIndex(
            name: "IX_smart_view_sync_id",
            table: "smart_view",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_section_sync_id",
            table: "session_section",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_pack_sync_id",
            table: "session_pack",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_item_sync_id",
            table: "session_item",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_note_sync_id",
            table: "note",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_entry_sync_id",
            table: "entry",
            column: "sync_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_collection_sync_id",
            table: "collection",
            column: "sync_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "sync_state");

        migrationBuilder.DropIndex(
            name: "IX_smart_view_sync_id",
            table: "smart_view");

        migrationBuilder.DropIndex(
            name: "IX_session_section_sync_id",
            table: "session_section");

        migrationBuilder.DropIndex(
            name: "IX_session_pack_sync_id",
            table: "session_pack");

        migrationBuilder.DropIndex(
            name: "IX_session_item_sync_id",
            table: "session_item");

        migrationBuilder.DropIndex(
            name: "IX_note_sync_id",
            table: "note");

        migrationBuilder.DropIndex(
            name: "IX_entry_sync_id",
            table: "entry");

        migrationBuilder.DropIndex(
            name: "IX_collection_sync_id",
            table: "collection");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "smart_view");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "session_section");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "session_pack");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "session_item");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "note");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "entry");

        migrationBuilder.DropColumn(
            name: "sync_id",
            table: "collection");
    }
}
