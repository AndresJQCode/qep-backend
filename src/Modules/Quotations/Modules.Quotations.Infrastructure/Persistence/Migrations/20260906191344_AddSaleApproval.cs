using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quien reviso la venta y cuando.
    ///
    /// Una venta ahora nace `Pending` y otra persona la aprueba: por eso estas dos columnas van
    /// aparte de converted_at/converted_by, que son de quien la registro. Nullables, porque
    /// mientras nadie revisa no hay nada que guardar.
    ///
    /// Sin backfill: las ventas que ya existen quedan `Approved` sin revisor. Se crearon cuando
    /// convertir **era** aprobar, y ponerles un nombre ahi diria que alguien las reviso.
    /// </summary>
    public partial class AddSaleApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "approved_at",
                schema: "quotations",
                table: "sales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by",
                schema: "quotations",
                table: "sales",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approved_at",
                schema: "quotations",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "approved_by",
                schema: "quotations",
                table: "sales");
        }
    }
}
