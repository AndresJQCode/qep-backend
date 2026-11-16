using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-16 §6: «Fecha del pedido» (<c>order_date</c>), aparte de <c>converted_at</c>. Los pedidos existentes
    /// quedan con el día local de <c>converted_at</c> en el huso de su tenant (F-7), así que filtros, exportación, reportes
    /// y PDF dan lo mismo que antes hasta que alguien cambie una fecha.
    /// </summary>
    public partial class AddOrderDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Nullable, para poder llenarla antes de exigirla.
            migrationBuilder.AddColumn<DateOnly>(
                name: "order_date",
                schema: "quotations",
                table: "orders",
                type: "date",
                nullable: true);

            // 2. Backfill. tenancy.tenants.time_zone es nullable; America/Bogota es el default del registro
            //    (RegistrationEndpoints.cs). Tenancy migra antes que Quotations en Program.cs, así que en la API la tabla existe;
            //    la guarda deja correr la migración en una base que sólo tiene Quotations (las pruebas de migración), como
            //    AddQuotationSettings y AddPdfSections. Lo que quede sin tenant toma el mismo default, o el NOT NULL de abajo
            //    no pasaría.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('tenancy.tenants') IS NOT NULL THEN
                        UPDATE quotations.orders o
                        SET order_date = (o.converted_at AT TIME ZONE COALESCE(NULLIF(t.time_zone, ''), 'America/Bogota'))::date
                        FROM tenancy.tenants t
                        WHERE t.id = o.tenant_id;
                    END IF;

                    UPDATE quotations.orders
                    SET order_date = (converted_at AT TIME ZONE 'America/Bogota')::date
                    WHERE order_date IS NULL;
                END $$;
                """);

            // 3. Obligatoria, y el índice del orden nuevo. El de (tenant_id, converted_at, order_number) se queda: lo usa la
            //    ventana de sincronización.
            migrationBuilder.AlterColumn<DateOnly>(
                name: "order_date",
                schema: "quotations",
                table: "orders",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            // Expand/contract (CLAUDE.md, spec 2026-10-29 D-5): durante el rollout los pods viejos insertan pedidos sin
            // order_date, y sin este default el NOT NULL los rechaza. Va sólo en la base, no en el modelo de EF: el código
            // nuevo siempre escribe la fecha. Una migración posterior lo quita cuando ya no quede ningún pod viejo (contract).
            // El Down no lo quita aparte: DropColumn se lo lleva con la columna.
            migrationBuilder.Sql(
                "ALTER TABLE quotations.orders ALTER COLUMN order_date SET DEFAULT ((now() AT TIME ZONE 'America/Bogota')::date);");

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_order_date_converted_at_number",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "order_date", "converted_at", "order_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_order_date_converted_at_number",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "order_date",
                schema: "quotations",
                table: "orders");
        }
    }
}
