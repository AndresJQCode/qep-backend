using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCitySynonyms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "city_synonyms",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_city_synonyms", x => x.id);
                    table.ForeignKey(
                        name: "FK_city_synonyms_cities_city_id",
                        column: x => x.city_id,
                        principalSchema: "geography",
                        principalTable: "cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_city_synonyms_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "geography",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unresolved_cities",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    sample_address = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    province_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    synonym_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unresolved_cities", x => x.id);
                    table.CheckConstraint("CK_unresolved_cities_status", "status IN ('Pending','Resolved','Discarded')");
                    table.ForeignKey(
                        name: "FK_unresolved_cities_city_synonyms_synonym_id",
                        column: x => x.synonym_id,
                        principalSchema: "geography",
                        principalTable: "city_synonyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_unresolved_cities_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "geography",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unresolved_city_occurrences",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unresolved_city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    party_city_only = table.Column<bool>(type: "boolean", nullable: false),
                    customer_city_only = table.Column<bool>(type: "boolean", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unresolved_city_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_unresolved_city_occurrences_unresolved_cities_unresolved_ci~",
                        column: x => x.unresolved_city_id,
                        principalSchema: "geography",
                        principalTable: "unresolved_cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_city_synonyms_city",
                schema: "geography",
                table: "city_synonyms",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "IX_city_synonyms_department_text_active",
                schema: "geography",
                table: "city_synonyms",
                columns: new[] { "department_id", "normalized_text" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_cities_department_text_open",
                schema: "geography",
                table: "unresolved_cities",
                columns: new[] { "department_id", "normalized_text" },
                unique: true,
                filter: "status <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_cities_status_last_seen",
                schema: "geography",
                table: "unresolved_cities",
                columns: new[] { "status", "last_seen_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_cities_synonym_id",
                schema: "geography",
                table: "unresolved_cities",
                column: "synonym_id");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_city_occurrences_source_ref",
                schema: "geography",
                table: "unresolved_city_occurrences",
                columns: new[] { "source", "source_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_city_occurrences_unresolved_city",
                schema: "geography",
                table: "unresolved_city_occurrences",
                column: "unresolved_city_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unresolved_city_occurrences",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "unresolved_cities",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "city_synonyms",
                schema: "geography");
        }
    }
}
