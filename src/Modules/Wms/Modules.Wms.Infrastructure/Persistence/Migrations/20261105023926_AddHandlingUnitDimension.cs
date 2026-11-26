using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHandlingUnitDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D-12: la unidad logística entra en la clave del saldo y en el movimiento. Las tablas están vacías en todo ambiente (la rama
            // no llegó a develop), así que soltar y recrear el índice único no reescribe datos.
            migrationBuilder.DropIndex(
                name: "UQ_stock_balances_key",
                schema: "wms",
                table: "stock_balances");

            migrationBuilder.AddColumn<Guid>(
                name: "handling_unit_id",
                schema: "wms",
                table: "stock_balances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "handling_unit_id",
                schema: "wms",
                table: "balance_checks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_stock_balances_key",
                schema: "wms",
                table: "stock_balances",
                columns: new[] { "tenant_id", "owner_id", "location_id", "variant_id", "lot_id", "handling_unit_id", "status" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // stock_movements está fuera de las migraciones de EF y la vista es SQL crudo: van al final, con las columnas ya creadas.
            migrationBuilder.Sql(HandlingUnitDimensionSql.AddMovementColumns);
            migrationBuilder.Sql(HandlingUnitDimensionSql.ReplaceStockRowsView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Vacío a propósito, como el de AddInventoryLedger (S11): el ledger es prueba de negocio y una columna de la clave no se
            // quita con datos vivos. Un rollback es revertir el código y dejar las columnas, que son nullable. Ojo con el índice: el
            // upsert del código viejo hace ON CONFLICT sobre la clave de seis columnas, que ya no existe, y respondería 42P10 en cada
            // movimiento. Tras revertir el código hay que recrear a mano UQ_stock_balances_key sin handling_unit_id, y eso sólo pasa
            // si ningún saldo tiene unidad.
        }
    }
}
