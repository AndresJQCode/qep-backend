using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Hr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalParameterChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "hr",
                table: "legal_parameters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                schema: "hr",
                table: "legal_parameters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "hr",
                table: "legal_parameter_brackets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                schema: "hr",
                table: "legal_parameter_brackets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "legal_parameter_changes",
                schema: "hr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_parameter_changes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_legal_parameter_changes_changed_at",
                schema: "hr",
                table: "legal_parameter_changes",
                column: "changed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_parameter_changes",
                schema: "hr");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "hr",
                table: "legal_parameters");

            migrationBuilder.DropColumn(
                name: "updated_by",
                schema: "hr",
                table: "legal_parameters");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "hr",
                table: "legal_parameter_brackets");

            migrationBuilder.DropColumn(
                name: "updated_by",
                schema: "hr",
                table: "legal_parameter_brackets");
        }
    }
}
