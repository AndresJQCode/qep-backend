using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Suppliers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Tres cosas que el modelo de EF no puede expresar y esta migración agrega a mano:
    //   1. La extensión pg_trgm, de la que dependen los índices GIN (IF NOT EXISTS: otro módulo
    //      pudo habilitarla ya y éste no puede asumir el orden de las migraciones ajenas).
    //   2. Los dos índices sobre columnas del complex type Identification (EF Core no admite
    //      HasIndex sobre ellas): el único por documento, SIN filtro parcial (spec D-5: también
    //      entre inactivos), y el trigram del número para el ILIKE del listado.
    //   3. La FK a geography.cities(id): otro esquema, de otro módulo, de otro DbContext. No
    //      aparece en el snapshot. Requiere que geography.cities exista: Program.cs inicializa
    //      Geography antes que Suppliers a propósito. Mismo criterio que AddCompanyCity.
    public partial class InitialSuppliers : Migration
    {
        private const string CityForeignKey = "FK_suppliers_cities_city_id";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.EnsureSchema(
                name: "suppliers");

            migrationBuilder.CreateTable(
                name: "suppliers",
                schema: "suppliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    business_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    city_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    phone_e164 = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    payment_term_days = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    identification_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_bank_accounts",
                schema: "suppliers",
                columns: table => new
                {
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    account_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_bank_accounts", x => new { x.supplier_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_supplier_bank_accounts_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "suppliers",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_contacts",
                schema: "suppliers",
                columns: table => new
                {
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_contacts", x => new { x.supplier_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_supplier_contacts_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "suppliers",
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_business_name_trgm",
                schema: "suppliers",
                table: "suppliers",
                column: "business_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_city",
                schema: "suppliers",
                table: "suppliers",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_name_trgm",
                schema: "suppliers",
                table: "suppliers",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_tenant_category",
                schema: "suppliers",
                table: "suppliers",
                columns: new[] { "tenant_id", "category" });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_tenant_identification",
                schema: "suppliers",
                table: "suppliers",
                columns: new[] { "tenant_id", "identification_type", "identification_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_identification_number_trgm",
                schema: "suppliers",
                table: "suppliers",
                column: "identification_number")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.AddForeignKey(
                name: CityForeignKey,
                schema: "suppliers",
                table: "suppliers",
                column: "city_id",
                principalSchema: "geography",
                principalTable: "cities",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: CityForeignKey,
                schema: "suppliers",
                table: "suppliers");

            migrationBuilder.DropTable(
                name: "supplier_bank_accounts",
                schema: "suppliers");

            migrationBuilder.DropTable(
                name: "supplier_contacts",
                schema: "suppliers");

            migrationBuilder.DropTable(
                name: "suppliers",
                schema: "suppliers");
        }
    }
}
