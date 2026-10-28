using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-10-26 D-5: <c>customers.data_consents</c>, el registro de consentimiento de datos (sólo de inserción).
    ///
    /// La FK a <c>customers.customers</c> va a mano: el modelo no la declara (ver <c>CustomersDbContext.ConfigureDataConsent</c>)
    /// para que EF no agregue por convención un índice sobre <c>customer_id</c> solo, que el de orden ya cubre. RESTRICT: la fila
    /// es prueba legal y no puede desaparecer con su cliente.
    /// </summary>
    public partial class AddCustomerDataConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_consents",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    channel_user_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    wa_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shown_text = table.Column<string>(type: "text", nullable: true),
                    prompt_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replied_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reply_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reply_wamid = table.Column<string>(type: "text", nullable: true),
                    chatbot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chatbot_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    chatbot_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chatbot_version_number = table.Column<int>(type: "integer", nullable: true),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    medium = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_consents", x => x.id);
                    table.ForeignKey(
                        name: "FK_data_consents_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.CheckConstraint("CK_data_consents_actor", "(event IN ('Accepted', 'Rejected') AND actor_kind = 'Chatbot' AND actor_user_id IS NULL) OR (event = 'Revoked' AND actor_kind = 'Member' AND actor_user_id IS NOT NULL)");
                    table.CheckConstraint("CK_data_consents_channel", "channel IS NULL OR channel IN ('WhatsApp', 'Instagram')");
                    table.CheckConstraint("CK_data_consents_chatbot_fields", "(event = 'Revoked' AND shown_text IS NULL AND prompt_message_id IS NULL AND replied_to_message_id IS NULL AND reply_message_id IS NULL AND reply_wamid IS NULL AND chatbot_id IS NULL AND chatbot_name IS NULL AND chatbot_version_id IS NULL AND chatbot_version_number IS NULL AND node_id IS NULL) OR (event <> 'Revoked' AND shown_text IS NOT NULL AND reply_message_id IS NOT NULL AND chatbot_id IS NOT NULL AND chatbot_name IS NOT NULL AND chatbot_version_id IS NOT NULL AND chatbot_version_number IS NOT NULL AND node_id IS NOT NULL)");
                    table.CheckConstraint("CK_data_consents_event", "event IN ('Accepted', 'Rejected', 'Revoked')");
                    table.CheckConstraint("CK_data_consents_identity", "(event = 'Revoked' AND medium = 'Other' AND conversation_id IS NULL AND channel IS NULL AND channel_user_id IS NULL AND wa_id IS NULL) OR ((event <> 'Revoked' OR medium = 'ThisConversation') AND conversation_id IS NOT NULL AND channel IS NOT NULL AND channel_user_id IS NOT NULL)");
                    table.CheckConstraint("CK_data_consents_medium", "medium IS NULL OR medium IN ('ThisConversation', 'Other')");
                    table.CheckConstraint("CK_data_consents_revocation_fields", "(event = 'Revoked' AND medium IS NOT NULL AND note IS NOT NULL AND length(btrim(note)) BETWEEN 1 AND 500) OR (event <> 'Revoked' AND medium IS NULL AND note IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_consents_reply_message",
                schema: "customers",
                table: "data_consents",
                column: "reply_message_id",
                unique: true,
                filter: "reply_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_data_consents_tenant_customer_order",
                schema: "customers",
                table: "data_consents",
                columns: new[] { "tenant_id", "customer_id", "occurred_at", "sequence" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_consents",
                schema: "customers");
        }
    }
}
