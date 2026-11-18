using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationWhatsAppNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone",
                schema: "wholesale",
                table: "distributors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "whatsapp_connection_id",
                schema: "wholesale",
                table: "distributors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "generation",
                schema: "wholesale",
                table: "distributor_invitations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "notice_failure_code",
                schema: "wholesale",
                table: "distributor_invitations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notice_status",
                schema: "wholesale",
                table: "distributor_invitations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "phone",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "whatsapp_connection_id",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "generation",
                schema: "wholesale",
                table: "distributor_invitations");

            migrationBuilder.DropColumn(
                name: "notice_failure_code",
                schema: "wholesale",
                table: "distributor_invitations");

            migrationBuilder.DropColumn(
                name: "notice_status",
                schema: "wholesale",
                table: "distributor_invitations");
        }
    }
}
