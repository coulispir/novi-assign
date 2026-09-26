using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core.Service.Migrations
{
    /// <inheritdoc />
    public partial class RemoveWalletAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AccountWallets_AccountId_Currency",
                table: "AccountWallets");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "AccountWallets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "AccountWallets",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_AccountWallets_AccountId_Currency",
                table: "AccountWallets",
                columns: new[] { "AccountId", "Currency" },
                unique: true);
        }
    }
}
