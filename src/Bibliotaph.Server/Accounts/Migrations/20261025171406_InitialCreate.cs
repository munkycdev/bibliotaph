using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261025171406_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "invites",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                code_hash = table.Column<string>(type: "TEXT", nullable: false),
                note = table.Column<string>(type: "TEXT", nullable: true),
                admin = table.Column<bool>(type: "INTEGER", nullable: false),
                created_by = table.Column<string>(type: "TEXT", nullable: true),
                created = table.Column<DateTime>(type: "TEXT", nullable: false),
                expires = table.Column<DateTime>(type: "TEXT", nullable: false),
                redeemed = table.Column<DateTime>(type: "TEXT", nullable: true),
                redeemed_by = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_invites", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                name = table.Column<string>(type: "TEXT", nullable: false),
                email = table.Column<string>(type: "TEXT", nullable: true),
                is_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                created = table.Column<DateTime>(type: "TEXT", nullable: false),
                last_sign_in = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "logins",
            columns: table => new
            {
                provider = table.Column<string>(type: "TEXT", nullable: false),
                subject = table.Column<string>(type: "TEXT", nullable: false),
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                email = table.Column<string>(type: "TEXT", nullable: true),
                added = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_logins", x => new { x.provider, x.subject });
                table.ForeignKey(
                    name: "FK_logins_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "storage_connections",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                provider = table.Column<string>(type: "TEXT", nullable: false),
                account_id = table.Column<string>(type: "TEXT", nullable: false),
                display_name = table.Column<string>(type: "TEXT", nullable: true),
                protected_refresh_token = table.Column<string>(type: "TEXT", nullable: false),
                scopes = table.Column<string>(type: "TEXT", nullable: true),
                created = table.Column<DateTime>(type: "TEXT", nullable: false),
                updated = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_storage_connections", x => x.id);
                table.ForeignKey(
                    name: "FK_storage_connections_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_invites_code_hash",
            table: "invites",
            column: "code_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_logins_user_id",
            table: "logins",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_storage_connections_user_id_provider_account_id",
            table: "storage_connections",
            columns: new[] { "user_id", "provider", "account_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "invites");

        migrationBuilder.DropTable(
            name: "logins");

        migrationBuilder.DropTable(
            name: "storage_connections");

        migrationBuilder.DropTable(
            name: "users");
    }
}
