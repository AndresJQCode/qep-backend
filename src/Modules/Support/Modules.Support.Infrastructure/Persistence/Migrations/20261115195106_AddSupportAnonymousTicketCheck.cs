using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportAnonymousTicketCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_anonymous",
                schema: "support",
                table: "tickets",
                sql: "NOT is_anonymous OR (customer_id IS NULL AND requester_first_names IS NULL AND requester_last_names IS NULL AND requester_business_name IS NULL AND requester_document_type IS NULL AND requester_document_number IS NULL AND requester_email IS NULL AND requester_mobile_phone IS NULL AND requester_landline_phone IS NULL AND requester_country_code IS NULL AND requester_address IS NULL AND requester_city_id IS NULL AND requester_city_name IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_anonymous",
                schema: "support",
                table: "tickets");
        }
    }
}
