using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bibliotaph.Server.Accounts.Migrations;

/// <inheritdoc />
public partial class _20261025175319_StorageConnectionState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "account_kind",
            table: "storage_connections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "needs_reconnect",
            table: "storage_connections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "notification_secret",
            table: "storage_connections",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTime>(
            name: "subscription_expires",
            table: "storage_connections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "subscription_id",
            table: "storage_connections",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "account_kind",
            table: "storage_connections");

        migrationBuilder.DropColumn(
            name: "needs_reconnect",
            table: "storage_connections");

        migrationBuilder.DropColumn(
            name: "notification_secret",
            table: "storage_connections");

        migrationBuilder.DropColumn(
            name: "subscription_expires",
            table: "storage_connections");

        migrationBuilder.DropColumn(
            name: "subscription_id",
            table: "storage_connections");
    }
}
