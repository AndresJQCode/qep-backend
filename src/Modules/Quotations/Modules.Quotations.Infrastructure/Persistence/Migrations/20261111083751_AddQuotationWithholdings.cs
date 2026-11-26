using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationWithholdings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "customer_with_retention",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<string>(
                name: "billing_company_country",
                schema: "quotations",
                table: "quotations",
                type: "char(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_company_country",
                schema: "quotations",
                table: "orders",
                type: "char(2)",
                maxLength: 2,
                nullable: true);

            // Plan impuestos por tipo P1: el país de la empresa se congela con la cuenta. Las filas que ya tienen cuenta
            // lo toman de companies.companies; sin esa tabla (Companies no migró en esta base, P6/ruling C11) o sin la
            // empresa, Colombia, que es el único país en que se facturaba hasta hoy. Una cotización sin cuenta queda en null.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('companies.companies') IS NOT NULL THEN
                        UPDATE quotations.quotations AS q
                        SET billing_company_country = upper(c.country)
                        FROM companies.companies AS c
                        WHERE c.id = q.billing_company_id AND q.billing_company_country IS NULL;

                        UPDATE quotations.orders AS o
                        SET billing_company_country = upper(c.country)
                        FROM companies.companies AS c
                        WHERE c.id = o.billing_company_id AND o.billing_company_country IS NULL;
                    END IF;

                    UPDATE quotations.quotations
                    SET billing_company_country = 'CO'
                    WHERE billing_company_id IS NOT NULL AND billing_company_country IS NULL;

                    UPDATE quotations.orders
                    SET billing_company_country = 'CO'
                    WHERE billing_company_id IS NOT NULL AND billing_company_country IS NULL;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "quotation_withholdings",
                schema: "quotations",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    country = table.Column<string>(type: "char(2)", maxLength: 2, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    minimum_base = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    minimum_base_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    tax_unit_value = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    base_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_withholdings", x => new { x.document_id, x.source, x.position });
                    table.CheckConstraint("CK_quotation_withholdings_source", "source IN ('Customer','Billing')");
                    table.ForeignKey(
                        name: "FK_quotation_withholdings_quotations_document_id",
                        column: x => x.document_id,
                        principalSchema: "quotations",
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quotation_withholdings",
                schema: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_company_country",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "billing_company_country",
                schema: "quotations",
                table: "orders");

            migrationBuilder.AlterColumn<bool>(
                name: "customer_with_retention",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);
        }
    }
}
