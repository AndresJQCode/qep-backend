using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveriesProviderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_provider_id",
                schema: "messaging",
                table: "webhook_deliveries",
                columns: new[] { "provider", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_provider_id",
                schema: "messaging",
                table: "webhook_deliveries");
        }
    }
}
