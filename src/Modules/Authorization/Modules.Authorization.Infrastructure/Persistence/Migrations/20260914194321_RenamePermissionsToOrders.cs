using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Authorization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenamePermissionsToOrders : Migration
    {
        // Datos, no esquema (spec 2026-09-14, D4). Los roles custom guardan sus códigos en texto, y
        // sin esto perderían el acceso sin error. array_replace conserva la posición en la lista.
        // Los roles de fábrica viven en código (QepServiceCollectionExtensions) y no pasan por acá.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.sale.read',   'quotations.order.read');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.sale.manage', 'quotations.order.manage');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'reporting.sales.read',   'reporting.orders.read');""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'reporting.orders.read',   'reporting.sales.read');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.order.manage', 'quotations.sale.manage');""");
            migrationBuilder.Sql("""UPDATE "authorization".roles SET permissions = array_replace(permissions, 'quotations.order.read',   'quotations.sale.read');""");
        }
    }
}
