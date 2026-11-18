using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 §4.4: banco y número de la cuenta de la tienda pasan a opcionales en el modelo (null los dos es la
    /// empresa sin cuenta). No hay DDL: <c>billing_bank_name</c> y <c>billing_account_number</c> ya son nullable en
    /// <c>shopify.stores</c> desde <c>InitialShopify</c>, porque <c>Billing</c> es un owned opcional (la tienda sin configurar
    /// no lo tiene). La migración sólo mueve el snapshot del modelo. Aun así el despliegue del spec 2026-11-17 va con corte
    /// (R5): un pod viejo leería una fila sin cuenta como una tienda sin <c>Billing</c> y dejaría sus pedidos en
    /// <c>store_not_configured</c>.
    /// </summary>
    public partial class OptionalShopifyStoreBillingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
