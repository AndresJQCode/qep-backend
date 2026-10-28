using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionSendBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_send_budgets",
                schema: "messaging",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tokens = table.Column<double>(type: "double precision", nullable: false),
                    rate = table.Column<double>(type: "double precision", nullable: false),
                    refilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    throttled_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_send_budgets", x => x.connection_id);
                    table.CheckConstraint("CK_connection_send_budgets_rate", "rate >= 1");
                    table.CheckConstraint("CK_connection_send_budgets_tokens", "tokens >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_send_budgets",
                schema: "messaging");
        }
    }
}
