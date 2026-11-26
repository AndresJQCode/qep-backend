using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Ai.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "copilot_questions",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_questions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_copilot_questions_asked_at",
                schema: "ai",
                table: "copilot_questions",
                column: "asked_at");

            migrationBuilder.CreateIndex(
                name: "IX_copilot_questions_member",
                schema: "ai",
                table: "copilot_questions",
                columns: new[] { "tenant_id", "user_id", "asked_at" });

            // Revisión de la rama, hallazgo 4: el tope sigue contando las preguntas de antes del registro (las de hilos que existen; las de
            // hilos ya borrados se perdieron con ellos). Dos días, como guarda el barrido.
            migrationBuilder.Sql("""
                INSERT INTO ai.copilot_questions (id, tenant_id, user_id, asked_at)
                SELECT m.id, t.tenant_id, t.user_id, m.created_at
                  FROM ai.copilot_messages m
                  JOIN ai.copilot_threads t ON t.id = m.thread_id
                 WHERE m.role = 'User' AND m.created_at >= now() - interval '2 days';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copilot_questions",
                schema: "ai");
        }
    }
}
