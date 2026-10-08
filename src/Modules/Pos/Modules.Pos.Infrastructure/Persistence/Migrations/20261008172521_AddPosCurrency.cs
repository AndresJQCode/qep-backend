using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "pos",
                table: "sales",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "COP");

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "pos",
                table: "cash_sessions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "COP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "currency",
                schema: "pos",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "pos",
                table: "cash_sessions");
        }
    }
}
