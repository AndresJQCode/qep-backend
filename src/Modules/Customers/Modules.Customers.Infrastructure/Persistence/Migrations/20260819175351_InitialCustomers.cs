using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "cuc_counters",
                schema: "customers",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cuc_counters", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cuc = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    identification_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    department = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    with_retention = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant",
                schema: "customers",
                table: "customers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_cuc",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "cuc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_identification",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "identification_type", "identification_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cuc_counters",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "customers");
        }
    }
}
