using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignRecipientsMessageIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_message",
                schema: "messaging",
                table: "campaign_recipients",
                column: "message_id",
                filter: "message_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_campaign_recipients_message",
                schema: "messaging",
                table: "campaign_recipients");
        }
    }
}
