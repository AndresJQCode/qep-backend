using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMessaging : Migration
    {
        /// <summary>Spec 2026-10-09 §7.3: extensiones en public (precedente pg_trgm, memoria «pg_trgm necesita el
        /// schema public») y la configuración de búsqueda, idempotente porque CREATE TEXT SEARCH CONFIGURATION
        /// no tiene IF NOT EXISTS y las bases locales pueden tenerla a medias.</summary>
        public const string SearchConfigurationSql = """
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS unaccent;
            CREATE EXTENSION IF NOT EXISTS btree_gin;
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace
                    WHERE n.nspname = 'messaging' AND c.cfgname = 'es_unaccent')
                THEN
                    CREATE TEXT SEARCH CONFIGURATION messaging.es_unaccent (COPY = pg_catalog.spanish);
                    ALTER TEXT SEARCH CONFIGURATION messaging.es_unaccent
                        ALTER MAPPING FOR hword, hword_part, word, asciiword, asciihword, hword_asciipart
                        WITH unaccent, spanish_stem;
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            // Antes de cualquier tabla: search_vector de messages referencia messaging.es_unaccent, y los
            // índices trigram y el GIN con tenant_id necesitan pg_trgm y btree_gin.
            migrationBuilder.Sql(SearchConfigurationSql);

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wa_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    profile_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    unread_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_inbound_wamid = table.Column<string>(type: "text", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_message_direction = table.Column<short>(type: "smallint", nullable: true),
                    last_message_kind = table.Column<short>(type: "smallint", nullable: true),
                    last_message_preview = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_message_status = table.Column<short>(type: "smallint", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id);
                    table.CheckConstraint("CK_conversations_status", "status IN ('Open','Resolved')");
                    table.CheckConstraint("CK_conversations_unread", "unread_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    body_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    wamid = table.Column<string>(type: "text", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_by_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<int>(type: "integer", nullable: true),
                    failure_title = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('messaging.es_unaccent', coalesce(text, '') || ' ' || coalesce(caption, ''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                    table.CheckConstraint("CK_messages_direction", "direction IN (1, 2)");
                    table.CheckConstraint("CK_messages_inbound_not_failed", "direction = 2 OR status <> 4");
                    table.CheckConstraint("CK_messages_kind", "kind BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_messages_status", "status BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_messages_conversation",
                        column: x => x.conversation_id,
                        principalSchema: "messaging",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_media",
                schema: "messaging",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    meta_media_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    storage_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_media", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_message_media_message",
                        column: x => x.message_id,
                        principalSchema: "messaging",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_connection_wa",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "connection_id", "wa_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_profile_name_trgm",
                schema: "messaging",
                table: "conversations",
                column: "profile_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_open",
                schema: "messaging",
                table: "conversations",
                column: "tenant_id",
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_status_activity",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "tenant_id", "status", "last_activity_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_unread",
                schema: "messaging",
                table: "conversations",
                column: "tenant_id",
                filter: "unread_count > 0")
                .Annotation("Npgsql:IndexInclude", new[] { "unread_count" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_wa_id_trgm",
                schema: "messaging",
                table: "conversations",
                column: "wa_id")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_message_media_pending",
                schema: "messaging",
                table: "message_media",
                column: "next_attempt_at",
                filter: "stored_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_connection_wamid",
                schema: "messaging",
                table: "messages",
                columns: new[] { "connection_id", "wamid" },
                unique: true,
                filter: "wamid IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_client",
                schema: "messaging",
                table: "messages",
                columns: new[] { "conversation_id", "client_id" },
                unique: true,
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_search",
                schema: "messaging",
                table: "messages",
                columns: new[] { "tenant_id", "search_vector" })
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_messages_thread",
                schema: "messaging",
                table: "messages",
                columns: new[] { "conversation_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_body_sha256",
                schema: "messaging",
                table: "webhook_deliveries",
                column: "body_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_pending",
                schema: "messaging",
                table: "webhook_deliveries",
                column: "id",
                filter: "processed_at IS NULL");

            // §7.2 y §7.3: lo que EF no modela. fillfactor para que las actualizaciones sin columnas
            // indexadas sean HOT; la lista pendiente del GIN acotada y el autovacuum de messages más
            // agresivo, para que la búsqueda no recorra una lista pendiente grande.
            migrationBuilder.Sql("ALTER TABLE messaging.conversations SET (fillfactor = 80);");
            migrationBuilder.Sql("ALTER TABLE messaging.messages SET (fillfactor = 90);");
            migrationBuilder.Sql("ALTER INDEX messaging.\"IX_messages_tenant_search\" SET (gin_pending_list_limit = 2048);");
            migrationBuilder.Sql("ALTER TABLE messaging.messages SET (autovacuum_vacuum_scale_factor = 0.02);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_media",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "messaging");

            // Las extensiones se quedan: pg_trgm la usan Customers y Catalog.
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION IF EXISTS messaging.es_unaccent;");
        }
    }
}
