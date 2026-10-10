using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Catalog.Migrations;

/// <inheritdoc />
public partial class CatalogPackMerges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(
            name: "matched_document_id",
            table: "entry_join",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "INTEGER");

        migrationBuilder.AlterColumn<long>(
            name: "document_id",
            table: "entry_join",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "INTEGER");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(
            name: "matched_document_id",
            table: "entry_join",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L,
            oldClrType: typeof(long),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.AlterColumn<long>(
            name: "document_id",
            table: "entry_join",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L,
            oldClrType: typeof(long),
            oldType: "INTEGER",
            oldNullable: true);
    }
}
