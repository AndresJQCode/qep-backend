using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOwnerMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_membership_id",
                schema: "tenancy",
                table: "tenants",
                type: "uuid",
                nullable: true);

            // Backfill: hasta acá el owner se deducía de memberships.origin = 'registration'.
            // Se copia esa deducción una sola vez y queda escrita; de ahí en más la autoridad la
            // nombra el tenant. Si un tenant tuviera más de una fila con ese origen gana la más
            // vieja, que es la que creó el tenant. Los tenants sin ninguna quedan en NULL: sin
            // owner nadie es owner, que es mejor que blindar a una membresía al azar.
            migrationBuilder.Sql(
                """
                UPDATE tenancy.tenants AS t
                SET owner_membership_id = (
                    SELECT m.id
                    FROM tenancy.memberships AS m
                    WHERE m.tenant_id = t.id
                      AND m.origin = 'registration'
                    ORDER BY m.created_at, m.id
                    LIMIT 1)
                WHERE t.owner_membership_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_membership_id",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
