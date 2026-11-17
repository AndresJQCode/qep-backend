using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 (agente IA en chatbots) §5.4: la sesión en modo bloque. Aditiva: un pod viejo inserta sesiones sin <c>origin</c> y el
    /// default las deja <c>Connection</c>, que cumple <c>CK_sessions_origin</c>.
    /// <para><c>Down</c> quita columnas, índice y CHECK de origen y vuelve el CHECK de estado sin <c>Returned</c>: <b>falla</b> si hay alguna
    /// sesión <c>Returned</c> y se deshace entero. No se corre en producción; el rollback es revertir el código.</para>
    /// </summary>
    public partial class AddChatbotBlockSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_sessions_status",
                schema: "ai",
                table: "sessions");

            migrationBuilder.AddColumn<string>(
                name: "block",
                schema: "ai",
                table: "tests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "chatbot_id",
                schema: "ai",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delegation_id",
                schema: "ai",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "idle_minutes",
                schema: "ai",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "objective_summary",
                schema: "ai",
                table: "sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "ai",
                table: "sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Connection");

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "ai",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_delegation",
                schema: "ai",
                table: "sessions",
                column: "delegation_id",
                unique: true,
                filter: "delegation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sessions_origin",
                schema: "ai",
                table: "sessions",
                sql: "(origin = 'ChatbotBlock') = (delegation_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sessions_status",
                schema: "ai",
                table: "sessions",
                sql: "status IN ('Live','HandedOff','WindowClosed','Interrupted','Failed','Abandoned','Returned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_delegation",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sessions_origin",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sessions_status",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "block",
                schema: "ai",
                table: "tests");

            migrationBuilder.DropColumn(
                name: "chatbot_id",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "delegation_id",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "idle_minutes",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "objective_summary",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "ai",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "ai",
                table: "sessions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sessions_status",
                schema: "ai",
                table: "sessions",
                sql: "status IN ('Live','HandedOff','WindowClosed','Interrupted','Failed','Abandoned')");
        }
    }
}
