using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// La razon social del cliente (CLI-RS-01).
    ///
    /// Nullable y sin backfill: `name` pasa a ser el nombre de la persona de contacto, y buena
    /// parte del padron son personas, no empresas. Copiar el nombre existente a esta columna
    /// afirmaria que cada cliente es una empresa, que es justo lo que la columna viene a
    /// distinguir. Los que si lo sean se completan desde el formulario.
    /// </summary>
    public partial class AddCustomerBusinessName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "business_name",
                schema: "customers",
                table: "customers",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "business_name",
                schema: "customers",
                table: "customers");
        }
    }
}
