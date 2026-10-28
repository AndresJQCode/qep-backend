using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResolutionReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "resolution_reason_id",
                schema: "messaging",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resolution_reasons",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resolution_reasons", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_resolution_reason_id",
                schema: "messaging",
                table: "conversations",
                column: "resolution_reason_id");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_resolution_reasons",
                schema: "messaging",
                table: "conversations",
                column: "resolution_reason_id",
                principalSchema: "messaging",
                principalTable: "resolution_reasons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Spec 2026-10-15 §3.1: EF no modela índices por expresión (mismo caso que IX_tags_tenant_name en Customers).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_resolution_reasons_tenant_name\" ON messaging.resolution_reasons (tenant_id, lower(name)) WHERE deleted_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS messaging.\"IX_resolution_reasons_tenant_name\";");

            migrationBuilder.DropForeignKey(
                name: "FK_conversations_resolution_reasons",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropTable(
                name: "resolution_reasons",
                schema: "messaging");

            migrationBuilder.DropIndex(
                name: "IX_conversations_resolution_reason_id",
                schema: "messaging",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "resolution_reason_id",
                schema: "messaging",
                table: "conversations");
        }
    }
}
