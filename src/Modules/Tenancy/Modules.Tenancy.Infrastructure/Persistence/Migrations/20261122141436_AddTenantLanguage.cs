using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                // Los tenants existentes quedan en español latinoamericano, el mismo valor con que
                // Tenant.Create arranca (spec 2026-11-22, i18n): con "" el GET devolvería un idioma que
                // el PUT rechaza. El DEFAULT queda en la columna, así que un pod viejo que inserte un
                // tenant sin conocerla también deja es-419: la migración es aditiva.
                defaultValue: "es-419");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "language",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
