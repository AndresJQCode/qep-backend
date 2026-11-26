using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingAuditProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: audit.entries belongs to the Audit module (ExcludeFromMigrations). This migration only records the
            // write projection in the model snapshot.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
