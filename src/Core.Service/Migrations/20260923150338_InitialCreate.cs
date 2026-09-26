using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core.Service.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountWallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CurrencyValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CurrencyCode = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    RateDate = table.Column<DateTime>(type: "date", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyValues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AccountWallets_AccountId_Currency",
                table: "AccountWallets",
                columns: new[] { "AccountId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CurrencyValues_Code_RateDate",
                table: "CurrencyValues",
                columns: new[] { "CurrencyCode", "RateDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountWallets");

            migrationBuilder.DropTable(
                name: "CurrencyValues");
        }
    }
}
