using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.ServiceContracts.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceAdjustment : Migration
    {
        /// <summary>
        /// Lo que valen las columnas nuevas en las filas que ya existían (spec reajuste §6.8 y §7). Idempotente: sólo toca lo
        /// nulo. El período de un cobro es su vencimiento (hasta hoy eran lo mismo); un contrato existente queda <c>Manual</c>,
        /// que nunca retiene, con su revisión en el primer aniversario del ancla que caiga al menos 15 días después de la fecha
        /// UTC de la migración; una fila del historial salió de una edición. <c>make_interval</c> desde el ancla recorta el 29
        /// de febrero al 28 sin perder el día en los años bisiestos.
        /// </summary>
        internal const string BackfillSql =
            """
            UPDATE billing.charges SET period_date = due_date WHERE period_date IS NULL;
            UPDATE billing.services SET adjustment_mode = 'Manual' WHERE adjustment_mode IS NULL;
            UPDATE billing.service_price_changes SET origin = 'Edit' WHERE origin IS NULL;
            UPDATE billing.services AS service
            SET next_review_date = (
                SELECT min((service.first_due_date + make_interval(years => k))::date)
                FROM generate_series(1, 200) AS k
                WHERE (service.first_due_date + make_interval(years => k))::date >= (now() AT TIME ZONE 'utc')::date + 15)
            WHERE service.next_review_date IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No es aditiva para el código viejo (columnas NOT NULL sin default y otro único): se despliega con corte.
            // Primero nulas, después el relleno y recién entonces NOT NULL.
            migrationBuilder.AddColumn<string>(
                name: "adjustment_mode",
                schema: "billing",
                table: "services",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "held_since",
                schema: "billing",
                table: "services",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ipc_extra_points",
                schema: "billing",
                table: "services",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "next_review_date",
                schema: "billing",
                table: "services",
                type: "date",
                nullable: true);

            // Sin relleno: ningún contrato existente tiene períodos retenidos antes de una pausa.
            migrationBuilder.AddColumn<List<DateOnly>>(
                name: "carried_held_periods",
                schema: "billing",
                table: "services",
                type: "date[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "billing",
                table: "service_price_changes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "schedule_id",
                schema: "billing",
                table: "service_price_changes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "period_date",
                schema: "billing",
                table: "charges",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "price_unreviewed",
                schema: "billing",
                table: "charges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.AlterColumn<string>(
                name: "adjustment_mode",
                schema: "billing",
                table: "services",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "next_review_date",
                schema: "billing",
                table: "services",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "origin",
                schema: "billing",
                table: "service_price_changes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "period_date",
                schema: "billing",
                table: "charges",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_charges_service_period",
                schema: "billing",
                table: "charges",
                columns: new[] { "service_id", "period_date" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_charges_service_due",
                schema: "billing",
                table: "charges");

            migrationBuilder.CreateTable(
                name: "ipc_rates",
                schema: "billing",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<short>(type: "smallint", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    updated_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ipc_rates", x => new { x.tenant_id, x.year });
                    table.CheckConstraint("CK_ipc_rates_rate", "rate >= -10 AND rate <= 100");
                });

            migrationBuilder.CreateTable(
                name: "service_price_schedules",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    new_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ipc_year = table.Column<short>(type: "smallint", nullable: true),
                    ipc_rate = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    extra_points = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    applied_period_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_price_schedules", x => x.id);
                    table.CheckConstraint("CK_service_price_schedules_new_price", "new_price > 0");
                    table.CheckConstraint("CK_service_price_schedules_source", "source IN ('Manual','Ipc')");
                    table.CheckConstraint("CK_service_price_schedules_status", "status IN ('Pending','Applied','Cancelled')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_services_adjustment_mode",
                schema: "billing",
                table: "services",
                sql: "adjustment_mode IN ('Manual','Ipc')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_services_ipc_extra_points",
                schema: "billing",
                table: "services",
                sql: "ipc_extra_points >= 0 AND ipc_extra_points <= 50");

            migrationBuilder.AddCheckConstraint(
                name: "CK_service_price_changes_origin",
                schema: "billing",
                table: "service_price_changes",
                sql: "origin IN ('Edit','Scheduled','Ipc')");

            migrationBuilder.CreateIndex(
                name: "IX_service_price_schedules_pending",
                schema: "billing",
                table: "service_price_schedules",
                column: "service_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_service_price_schedules_tenant_source_year",
                schema: "billing",
                table: "service_price_schedules",
                columns: new[] { "tenant_id", "source", "ipc_year" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ipc_rates",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "service_price_schedules",
                schema: "billing");

            migrationBuilder.DropCheckConstraint(
                name: "CK_services_adjustment_mode",
                schema: "billing",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "CK_services_ipc_extra_points",
                schema: "billing",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "CK_service_price_changes_origin",
                schema: "billing",
                table: "service_price_changes");

            migrationBuilder.DropIndex(
                name: "IX_charges_service_period",
                schema: "billing",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "adjustment_mode",
                schema: "billing",
                table: "services");

            migrationBuilder.DropColumn(
                name: "carried_held_periods",
                schema: "billing",
                table: "services");

            migrationBuilder.DropColumn(
                name: "held_since",
                schema: "billing",
                table: "services");

            migrationBuilder.DropColumn(
                name: "ipc_extra_points",
                schema: "billing",
                table: "services");

            migrationBuilder.DropColumn(
                name: "next_review_date",
                schema: "billing",
                table: "services");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "billing",
                table: "service_price_changes");

            migrationBuilder.DropColumn(
                name: "schedule_id",
                schema: "billing",
                table: "service_price_changes");

            migrationBuilder.DropColumn(
                name: "period_date",
                schema: "billing",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "price_unreviewed",
                schema: "billing",
                table: "charges");

            migrationBuilder.CreateIndex(
                name: "IX_charges_service_due",
                schema: "billing",
                table: "charges",
                columns: new[] { "service_id", "due_date" },
                unique: true);
        }
    }
}
