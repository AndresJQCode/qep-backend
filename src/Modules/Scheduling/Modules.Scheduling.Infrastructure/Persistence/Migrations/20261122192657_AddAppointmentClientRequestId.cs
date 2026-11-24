using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentClientRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_request_id",
                schema: "scheduling",
                table: "appointments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_appointments_client_request",
                schema: "scheduling",
                table: "appointments",
                columns: new[] { "tenant_id", "client_request_id" },
                unique: true,
                filter: "client_request_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_appointments_client_request",
                schema: "scheduling",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "client_request_id",
                schema: "scheduling",
                table: "appointments");
        }
    }
}
