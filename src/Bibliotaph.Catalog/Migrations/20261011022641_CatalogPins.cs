using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPins : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pin",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                kind = table.Column<string>(type: "TEXT", nullable: false),
                position = table.Column<int>(type: "INTEGER", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: true),
                collection_id = table.Column<long>(type: "INTEGER", nullable: true),
                pack_id = table.Column<long>(type: "INTEGER", nullable: true),
                smart_view_id = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pin", x => x.id);
                table.CheckConstraint("ck_pin_target", "CASE kind\n    WHEN 'Entry' THEN entry_id IS NOT NULL AND coalesce(collection_id, pack_id, smart_view_id) IS NULL\n    WHEN 'Favorites' THEN coalesce(entry_id, collection_id, pack_id, smart_view_id) IS NULL\n    WHEN 'Collection' THEN collection_id IS NOT NULL AND coalesce(entry_id, pack_id, smart_view_id) IS NULL\n    WHEN 'Binder' THEN pack_id IS NOT NULL AND coalesce(entry_id, collection_id, smart_view_id) IS NULL\n    WHEN 'SavedSearch' THEN smart_view_id IS NOT NULL AND coalesce(entry_id, collection_id, pack_id) IS NULL\n    ELSE 0\nEND");
                table.ForeignKey(
                    name: "FK_pin_collection_collection_id",
                    column: x => x.collection_id,
                    principalTable: "collection",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_pin_entry_entry_id",
                    column: x => x.entry_id,
                    principalTable: "entry",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_pin_session_pack_pack_id",
                    column: x => x.pack_id,
                    principalTable: "session_pack",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_pin_smart_view_smart_view_id",
                    column: x => x.smart_view_id,
                    principalTable: "smart_view",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_pin_collection_id",
            table: "pin",
            column: "collection_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_pin_entry_id",
            table: "pin",
            column: "entry_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_pin_favorites",
            table: "pin",
            column: "kind",
            unique: true,
            filter: "kind = 'Favorites'");

        migrationBuilder.CreateIndex(
            name: "IX_pin_pack_id",
            table: "pin",
            column: "pack_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_pin_smart_view_id",
            table: "pin",
            column: "smart_view_id",
            unique: true);

        // The sidebar starts as it was, Favorites then the saved views by name, followed by
        // the collections that were pinned, by name.
        migrationBuilder.Sql("""
            INSERT INTO pin (kind, position) VALUES ('Favorites', 0);
            INSERT INTO pin (kind, position, smart_view_id)
                SELECT 'SavedSearch', ROW_NUMBER() OVER (ORDER BY name COLLATE NOCASE, id), id FROM smart_view;
            INSERT INTO pin (kind, position, collection_id)
                SELECT 'Collection', (SELECT count(*) FROM smart_view) + ROW_NUMBER() OVER (ORDER BY name COLLATE NOCASE, id), id
                FROM collection WHERE pinned = 1;
            """);

        migrationBuilder.DropColumn(
            name: "pinned",
            table: "collection");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "pinned",
            table: "collection",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("UPDATE collection SET pinned = 1 WHERE id IN (SELECT collection_id FROM pin WHERE collection_id IS NOT NULL);");

        migrationBuilder.DropTable(
            name: "pin");
    }
}
