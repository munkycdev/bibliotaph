using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261102015120_NornisWorldEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nornis_world_events",
            columns: table => new
            {
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                event_id = table.Column<string>(type: "TEXT", nullable: false),
                kind = table.Column<string>(type: "TEXT", nullable: false),
                world_id = table.Column<string>(type: "TEXT", nullable: false),
                world_name = table.Column<string>(type: "TEXT", nullable: false),
                occurred_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                received_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                applied_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nornis_world_events", x => new { x.user_id, x.event_id });
                table.ForeignKey(
                    name: "FK_nornis_world_events_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nornis_world_events_user_id_applied_utc",
            table: "nornis_world_events",
            columns: new[] { "user_id", "applied_utc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "nornis_world_events");
    }
}
