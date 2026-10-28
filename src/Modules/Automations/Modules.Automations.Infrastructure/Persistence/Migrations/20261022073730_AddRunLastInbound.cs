using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Automations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRunLastInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "last_inbound_message_id",
                schema: "automations",
                table: "conversation_runs",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_inbound_message_id",
                schema: "automations",
                table: "conversation_runs");
        }
    }
}
