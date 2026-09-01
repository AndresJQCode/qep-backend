using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipInvitationTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "invitation_token_hash",
                schema: "tenancy",
                table: "memberships",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memberships_invitation_token_hash",
                schema: "tenancy",
                table: "memberships",
                column: "invitation_token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memberships_invitation_token_hash",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "invitation_token_hash",
                schema: "tenancy",
                table: "memberships");
        }
    }
}
