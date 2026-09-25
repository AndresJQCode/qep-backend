using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrdersExportLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orders_export_layouts",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    columns = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders_export_layouts", x => x.tenant_id);
                });

            // EF no deja marcar como requerida la colección owned que se guarda como JSON
            // (`Navigation(...).IsRequired()` falla al construir el modelo), así que genera la columna
            // nullable. El agregado nunca la deja en null, y la base lo garantiza igual: spec
            // 2026-09-24, "columns jsonb NOT NULL".
            migrationBuilder.Sql("ALTER TABLE quotations.orders_export_layouts ALTER COLUMN columns SET NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders_export_layouts",
                schema: "quotations");
        }
    }
}
