using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Variantes: la marca de que la biblioteca de opciones de un tenant ya se sembró
    /// (<c>OptionLibraryProvisioner</c>). Sin ella, borrar «Talla» sin uso la haría volver en el
    /// siguiente listado. Tabla fuera del modelo de EF a propósito: sólo la toca el SQL crudo del
    /// aprovisionador, así que el snapshot no cambia.
    /// </summary>
    public partial class AddOptionLibraryProvisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE catalog.option_library_provisions (
                    tenant_id uuid NOT NULL,
                    provisioned_at timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_option_library_provisions" PRIMARY KEY (tenant_id)
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE catalog.option_library_provisions;");
        }
    }
}
