using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261026033628_Devices : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "devices",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                name = table.Column<string>(type: "TEXT", nullable: false),
                key_hash = table.Column<string>(type: "TEXT", nullable: false),
                created = table.Column<DateTime>(type: "TEXT", nullable: false),
                last_used = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_devices", x => x.id);
                table.ForeignKey(
                    name: "FK_devices_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_devices_key_hash",
            table: "devices",
            column: "key_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_devices_user_id",
            table: "devices",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "devices");
    }
}
