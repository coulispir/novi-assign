using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core.Service.Migrations
{
    /// <inheritdoc />
    public partial class ChangeWalletIdToLong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountWallets",
                table: "AccountWallets");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AccountWallets");

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "AccountWallets",
                type: "bigint",
                nullable: false)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountWallets",
                table: "AccountWallets",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountWallets",
                table: "AccountWallets");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AccountWallets");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "AccountWallets",
                type: "uniqueidentifier",
                nullable: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountWallets",
                table: "AccountWallets",
                column: "Id");
        }
    }
}
