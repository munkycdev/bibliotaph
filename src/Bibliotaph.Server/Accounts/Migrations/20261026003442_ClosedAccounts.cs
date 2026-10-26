using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261026003442_ClosedAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "closed_accounts",
            columns: table => new
            {
                user_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                closed = table.Column<DateTime>(type: "TEXT", nullable: false),
                purged = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_closed_accounts", x => x.user_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "closed_accounts");
    }
}
