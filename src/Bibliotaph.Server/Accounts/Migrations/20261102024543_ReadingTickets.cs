using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261102024543_ReadingTickets : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "reading_tickets",
            columns: table => new
            {
                code_hash = table.Column<string>(type: "TEXT", nullable: false),
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                world_id = table.Column<string>(type: "TEXT", nullable: false),
                document_id = table.Column<long>(type: "INTEGER", nullable: false),
                entry_id = table.Column<long>(type: "INTEGER", nullable: false),
                start_page = table.Column<int>(type: "INTEGER", nullable: true),
                viewer_name = table.Column<string>(type: "TEXT", nullable: true),
                renew = table.Column<string>(type: "TEXT", nullable: false),
                created_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                expires_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                redeemed_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reading_tickets", x => x.code_hash);
                table.ForeignKey(
                    name: "FK_reading_tickets_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_reading_tickets_expires_utc",
            table: "reading_tickets",
            column: "expires_utc");

        migrationBuilder.CreateIndex(
            name: "IX_reading_tickets_user_id",
            table: "reading_tickets",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "reading_tickets");
    }
}
