using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipAdvisorCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "advisor_code",
                schema: "tenancy",
                table: "memberships",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memberships_tenant_id_advisor_code",
                schema: "tenancy",
                table: "memberships",
                columns: new[] { "tenant_id", "advisor_code" },
                unique: true,
                filter: "advisor_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memberships_tenant_id_advisor_code",
                schema: "tenancy",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "advisor_code",
                schema: "tenancy",
                table: "memberships");
        }
    }
}
