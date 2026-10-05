using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AhoraCenit.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSqlLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SqlLogin",
                table: "Users",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SqlPassword",
                table: "Users",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SqlLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SqlPassword",
                table: "Users");
        }
    }
}
