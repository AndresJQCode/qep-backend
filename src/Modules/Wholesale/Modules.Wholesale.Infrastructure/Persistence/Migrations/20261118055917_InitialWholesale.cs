using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWholesale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wholesale");

            migrationBuilder.CreateTable(
                name: "price_lists",
                schema: "wholesale",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true, computedColumnSql: "lower(name)", stored: true),
                    category_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_lists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "distributors",
                schema: "wholesale",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    identification_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    contact_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advisor_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    distributor_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distributors", x => x.id);
                    table.ForeignKey(
                        name: "FK_distributors_price_lists_price_list_id",
                        column: x => x.price_list_id,
                        principalSchema: "wholesale",
                        principalTable: "price_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "distributor_invitations",
                schema: "wholesale",
                columns: table => new
                {
                    distributor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reserved_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distributor_invitations", x => x.distributor_id);
                    table.ForeignKey(
                        name: "FK_distributor_invitations_distributors_distributor_id",
                        column: x => x.distributor_id,
                        principalSchema: "wholesale",
                        principalTable: "distributors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_distributor_invitations_reserved_tenant",
                schema: "wholesale",
                table: "distributor_invitations",
                column: "reserved_tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_distributor_invitations_token_hash",
                schema: "wholesale",
                table: "distributor_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_distributors_price_list_id",
                schema: "wholesale",
                table: "distributors",
                column: "price_list_id");

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_created",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_distributor_tenant",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "distributor_tenant_id" },
                unique: true,
                filter: "distributor_tenant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_identification",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "identification_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_price_list",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "price_list_id" });

            migrationBuilder.CreateIndex(
                name: "IX_distributors_tenant_status",
                schema: "wholesale",
                table: "distributors",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_price_lists_tenant_name",
                schema: "wholesale",
                table: "price_lists",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "distributor_invitations",
                schema: "wholesale");

            migrationBuilder.DropTable(
                name: "distributors",
                schema: "wholesale");

            migrationBuilder.DropTable(
                name: "price_lists",
                schema: "wholesale");
        }
    }
}
