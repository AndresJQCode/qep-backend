using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskBoardIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tasks_board",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "warehouse_id", "status", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_list",
                schema: "wms",
                table: "tasks",
                columns: new[] { "tenant_id", "warehouse_id", "created_at", "number" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_board",
                schema: "wms",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_tasks_list",
                schema: "wms",
                table: "tasks");
        }
    }
}
