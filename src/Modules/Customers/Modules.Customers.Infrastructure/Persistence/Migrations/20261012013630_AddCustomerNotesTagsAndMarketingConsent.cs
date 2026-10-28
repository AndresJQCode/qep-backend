using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerNotesTagsAndMarketingConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "marketing_opt_out",
                schema: "customers",
                table: "customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "marketing_opt_out_at",
                schema: "customers",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "marketing_opt_out_source",
                schema: "customers",
                table: "customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_notes",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_notes", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_notes_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.id);
                    table.CheckConstraint("CK_tags_color", "color IN ('gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'purple')");
                });

            migrationBuilder.CreateTable(
                name: "customer_tags",
                schema: "customers",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_tags", x => new { x.customer_id, x.tag_id });
                    table.ForeignKey(
                        name: "FK_customer_tags_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_customer_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalSchema: "customers",
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers",
                sql: "marketing_opt_out_source IS NULL OR marketing_opt_out_source IN ('manual', 'meta')");

            migrationBuilder.CreateIndex(
                name: "IX_customer_notes_customer_id",
                schema: "customers",
                table: "customer_notes",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_notes_tenant_customer_created",
                schema: "customers",
                table: "customer_notes",
                columns: new[] { "tenant_id", "customer_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_customer_tags_tag_id",
                schema: "customers",
                table: "customer_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_tags_tenant_tag",
                schema: "customers",
                table: "customer_tags",
                columns: new[] { "tenant_id", "tag_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tags_tenant",
                schema: "customers",
                table: "tags",
                column: "tenant_id");

            // El nombre de una etiqueta es único por tenant sin distinguir mayúsculas («VIP» y «vip» son la misma).
            // EF no modela índices por expresión, así que va a mano; CustomersUnitOfWork lo traduce a 409
            // customers.tag.name_taken por este nombre (CustomersDbContext.TagNameIndex).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_tags_tenant_name\" ON customers.tags (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_notes",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customer_tags",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_marketing_opt_out_source",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "marketing_opt_out",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "marketing_opt_out_at",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "marketing_opt_out_source",
                schema: "customers",
                table: "customers");
        }
    }
}
