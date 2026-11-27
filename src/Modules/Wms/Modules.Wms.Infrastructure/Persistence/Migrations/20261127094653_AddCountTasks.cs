using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCountTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sólo expande: un CHECK que admite más valores (Count; lo planeado en cero) y una columna nula. Un pod con el código de
            // T1 sigue escribiendo filas válidas contra este esquema. Lo contrario no es seguro (ver «Despliegue de T2» en el README).
            migrationBuilder.DropCheckConstraint(
                name: "CK_tasks_type",
                schema: "wms",
                table: "tasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_task_lines_quantity",
                schema: "wms",
                table: "task_lines");

            migrationBuilder.AddColumn<decimal>(
                name: "quantity_counted",
                schema: "wms",
                table: "task_lines",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_tasks_type",
                schema: "wms",
                table: "tasks",
                sql: "type IN ('Move','Count')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_task_lines_quantity",
                schema: "wms",
                table: "task_lines",
                sql: "quantity_planned >= 0 AND quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned AND (quantity_counted IS NULL OR quantity_counted >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A propósito falla (23514) si ya existe una tarea Count o una línea con planeado en cero: no hay vuelta atrás una vez que
            // T2 escribió esos datos. Se corrige hacia adelante, no se revierte.
            migrationBuilder.DropCheckConstraint(
                name: "CK_tasks_type",
                schema: "wms",
                table: "tasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_task_lines_quantity",
                schema: "wms",
                table: "task_lines");

            migrationBuilder.DropColumn(
                name: "quantity_counted",
                schema: "wms",
                table: "task_lines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tasks_type",
                schema: "wms",
                table: "tasks",
                sql: "type IN ('Move')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_task_lines_quantity",
                schema: "wms",
                table: "task_lines",
                sql: "quantity_planned > 0 AND quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned");
        }
    }
}
