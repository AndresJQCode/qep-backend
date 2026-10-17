using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFreightChargeModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "disabled_freight_charge_modes",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_quotation_settings_disabled_freight_charge_modes",
                schema: "quotations",
                table: "tenant_quotation_settings",
                sql: "disabled_freight_charge_modes <@ ARRAY['Charged', 'OnDelivery', 'Free']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_quotation_settings_disabled_freight_charge_modes",
                schema: "quotations",
                table: "tenant_quotation_settings");

            migrationBuilder.DropColumn(
                name: "disabled_freight_charge_modes",
                schema: "quotations",
                table: "tenant_quotation_settings");
        }
    }
}
