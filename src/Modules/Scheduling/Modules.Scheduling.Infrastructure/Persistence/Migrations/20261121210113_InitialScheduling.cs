using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialScheduling : Migration
    {
        /// <summary>
        /// Spec 2026-11-21 (Agenda), D-8: la doble reserva la impide la base. <c>btree_gist</c> permite combinar «mismo recurso» (igualdad) con
        /// «rangos que se solapan» en una restricción de exclusión; las migraciones de Mensajería ya crean <c>btree_gin</c> del mismo paquete
        /// <c>contrib</c>. La restricción la agrega esta migración a mano porque EF no modela <c>EXCLUDE</c>.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_main = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "services",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    bookable_by_channel = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_services", x => x.id);
                    table.CheckConstraint("CK_services_buffer", "buffer_minutes BETWEEN 0 AND 240 AND buffer_minutes % 5 = 0");
                    table.CheckConstraint("CK_services_duration", "duration_minutes BETWEEN 5 AND 480 AND duration_minutes % 5 = 0");
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "scheduling",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmation_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    min_notice_minutes = table.Column<int>(type: "integer", nullable: false),
                    horizon_days = table.Column<int>(type: "integer", nullable: false),
                    client_cancel_cutoff_hours = table.Column<int>(type: "integer", nullable: false),
                    reminder_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reminder_offset_hours = table.Column<int>(type: "integer", nullable: false),
                    reminder_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reminder_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.tenant_id);
                    table.CheckConstraint("CK_settings_confirmation_mode", "confirmation_mode IN ('Automatic','Manual')");
                    table.CheckConstraint("CK_settings_cutoff", "client_cancel_cutoff_hours BETWEEN 0 AND 720");
                    table.CheckConstraint("CK_settings_horizon", "horizon_days BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_settings_min_notice", "min_notice_minutes BETWEEN 0 AND 10080");
                    table.CheckConstraint("CK_settings_reminder_offset", "reminder_offset_hours BETWEEN 1 AND 168");
                });

            migrationBuilder.CreateTable(
                name: "availability_blocks",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    weekly = table.Column<string>(type: "jsonb", nullable: false),
                    one_off = table.Column<string>(type: "jsonb", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    closes_on_holidays = table.Column<bool>(type: "boolean", nullable: false),
                    capacity_per_resource = table.Column<int>(type: "integer", nullable: false),
                    session_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    allow_client_choice = table.Column<bool>(type: "boolean", nullable: false),
                    assignment_rule = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    service_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    resource_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_blocks", x => x.id);
                    table.CheckConstraint("CK_availability_blocks_assignment_rule", "assignment_rule IN ('FirstFree')");
                    table.CheckConstraint("CK_availability_blocks_capacity", "capacity_per_resource BETWEEN 1 AND 500");
                    table.CheckConstraint("CK_availability_blocks_kind", "kind IN ('Weekly','OneOff')");
                    table.CheckConstraint("CK_availability_blocks_session_mode", "session_mode IS NULL OR session_mode IN ('FirstBookingOpens','PublishedSessions')");
                    table.CheckConstraint("CK_availability_blocks_validity", "valid_from IS NULL OR valid_to IS NULL OR valid_from <= valid_to");
                    table.ForeignKey(
                        name: "FK_availability_blocks_locations_location_id",
                        column: x => x.location_id,
                        principalSchema: "scheduling",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resources", x => x.id);
                    table.ForeignKey(
                        name: "FK_resources_locations_location_id",
                        column: x => x.location_id,
                        principalSchema: "scheduling",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "closures",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closures", x => x.id);
                    table.CheckConstraint("CK_closures_range", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "FK_closures_locations_location_id",
                        column: x => x.location_id,
                        principalSchema: "scheduling",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_closures_resources_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "scheduling",
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occupies_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    booked_count = table.Column<int>(type: "integer", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.CheckConstraint("CK_sessions_booked", "booked_count >= 0 AND booked_count <= capacity");
                    table.CheckConstraint("CK_sessions_capacity", "capacity >= 1");
                    table.CheckConstraint("CK_sessions_range", "ends_at > starts_at AND occupies_until >= ends_at");
                    table.ForeignKey(
                        name: "FK_sessions_resources_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "scheduling",
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sessions_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "scheduling",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "appointments",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reminder_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.id);
                    table.CheckConstraint("CK_appointments_range", "ends_at > starts_at");
                    table.CheckConstraint("CK_appointments_source", "source IN ('BackOffice','Channel')");
                    table.CheckConstraint("CK_appointments_status", "status IN ('Pending','Confirmed','Cancelled','Completed','NoShow')");
                    table.ForeignKey(
                        name: "FK_appointments_locations_location_id",
                        column: x => x.location_id,
                        principalSchema: "scheduling",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_resources_resource_id",
                        column: x => x.resource_id,
                        principalSchema: "scheduling",
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "scheduling",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "scheduling",
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "appointment_events",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_events", x => x.id);
                    table.CheckConstraint("CK_appointment_events_action", "action IN ('Created','Confirmed','Cancelled','Attended','NoShow','Rescheduled','ReminderSent')");
                    table.ForeignKey(
                        name: "FK_appointment_events_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalSchema: "scheduling",
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_events_appointment_at",
                schema: "scheduling",
                table: "appointment_events",
                columns: new[] { "appointment_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_location_id",
                schema: "scheduling",
                table: "appointments",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_reminder_due",
                schema: "scheduling",
                table: "appointments",
                column: "starts_at",
                filter: "status = 'Confirmed' AND reminder_sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_resource_id",
                schema: "scheduling",
                table: "appointments",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_service_id",
                schema: "scheduling",
                table: "appointments",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_session",
                schema: "scheduling",
                table: "appointments",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_tenant_customer",
                schema: "scheduling",
                table: "appointments",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_tenant_resource_start",
                schema: "scheduling",
                table: "appointments",
                columns: new[] { "tenant_id", "resource_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_blocks_location_id",
                schema: "scheduling",
                table: "availability_blocks",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_availability_blocks_tenant_location",
                schema: "scheduling",
                table: "availability_blocks",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "IX_closures_location_id",
                schema: "scheduling",
                table: "closures",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "IX_closures_resource_id",
                schema: "scheduling",
                table: "closures",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "IX_closures_tenant_location_start",
                schema: "scheduling",
                table: "closures",
                columns: new[] { "tenant_id", "location_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_locations_tenant_main",
                schema: "scheduling",
                table: "locations",
                column: "tenant_id",
                unique: true,
                filter: "is_main");

            migrationBuilder.CreateIndex(
                name: "IX_resources_location_member",
                schema: "scheduling",
                table: "resources",
                columns: new[] { "location_id", "member_id" },
                unique: true,
                filter: "member_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_resources_tenant_location",
                schema: "scheduling",
                table: "resources",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "IX_services_tenant",
                schema: "scheduling",
                table: "services",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_resource_service_start",
                schema: "scheduling",
                table: "sessions",
                columns: new[] { "resource_id", "service_id", "starts_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_service_id",
                schema: "scheduling",
                table: "sessions",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_tenant_start",
                schema: "scheduling",
                table: "sessions",
                columns: new[] { "tenant_id", "starts_at" });

            // Dos sesiones activas del mismo recurso no pueden solaparse en [starts_at, occupies_until): el margen del servicio ocupa, y dos
            // rangos que sólo se tocan en un extremo no chocan. Una sesión sin citas (is_active = false) no ocupa nada.
            migrationBuilder.Sql("""
                ALTER TABLE scheduling.sessions
                    ADD CONSTRAINT "EX_sessions_resource_occupancy"
                    EXCLUDE USING gist (resource_id WITH =, tstzrange(starts_at, occupies_until, '[)') WITH &&)
                    WHERE (is_active);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointment_events",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "availability_blocks",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "closures",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "appointments",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "resources",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "services",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "locations",
                schema: "scheduling");
        }
    }
}
