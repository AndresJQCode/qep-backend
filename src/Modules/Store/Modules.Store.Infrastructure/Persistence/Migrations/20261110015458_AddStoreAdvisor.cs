using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreAdvisor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "advisor_membership_id",
                schema: "store",
                table: "stores",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "advisor_membership_id",
                schema: "store",
                table: "stores");
        }
    }
}
