using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskEventRecordedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recorded_at",
                schema: "wms",
                table: "task_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Las filas de antes no tienen otra hora que la del hecho: se llenan con ella. Expand-only: el pod viejo no la escribe y cae en el
            // default.
            migrationBuilder.Sql("UPDATE wms.task_events SET recorded_at = occurred_at;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "recorded_at",
                schema: "wms",
                table: "task_events");
        }
    }
}
