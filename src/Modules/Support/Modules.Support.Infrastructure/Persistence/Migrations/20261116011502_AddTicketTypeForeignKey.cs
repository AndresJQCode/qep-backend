using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTypeForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fase 4a (plan G5). Aditiva: los pods viejos nunca borran tipos. Falla si algún ticket apunta a un tipo que no existe;
            // antes de desplegar, esto tiene que dar 0:
            // SELECT count(*) FROM support.tickets t
            //   WHERE NOT EXISTS (SELECT 1 FROM support.ticket_types y WHERE y.id = t.ticket_type_id);
            migrationBuilder.CreateIndex(
                name: "IX_tickets_ticket_type_id",
                schema: "support",
                table: "tickets",
                column: "ticket_type_id");

            migrationBuilder.AddForeignKey(
                name: "FK_tickets_ticket_types_ticket_type_id",
                schema: "support",
                table: "tickets",
                column: "ticket_type_id",
                principalSchema: "support",
                principalTable: "ticket_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tickets_ticket_types_ticket_type_id",
                schema: "support",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_ticket_type_id",
                schema: "support",
                table: "tickets");
        }
    }
}
