using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultFreightChargeMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_freight_charge_mode",
                schema: "quotations",
                table: "tenant_quotation_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_tenant_quotation_settings_default_freight_charge_mode",
                schema: "quotations",
                table: "tenant_quotation_settings",
                sql: "default_freight_charge_mode IS NULL OR (default_freight_charge_mode IN ('Charged', 'OnDelivery', 'Free') AND NOT default_freight_charge_mode = ANY (disabled_freight_charge_modes))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_tenant_quotation_settings_default_freight_charge_mode",
                schema: "quotations",
                table: "tenant_quotation_settings");

            migrationBuilder.DropColumn(
                name: "default_freight_charge_mode",
                schema: "quotations",
                table: "tenant_quotation_settings");
        }
    }
}
