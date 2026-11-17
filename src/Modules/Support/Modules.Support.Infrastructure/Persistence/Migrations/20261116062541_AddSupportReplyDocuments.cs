using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportReplyDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_reply_documents",
                schema: "support",
                columns: table => new
                {
                    reply_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    next_step = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    signer_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    signer_title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    annexes = table.Column<string>(type: "jsonb", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", nullable: false),
                    responded_on = table.Column<DateOnly>(type: "date", nullable: false),
                    document_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_reply_documents", x => x.reply_id);
                    table.CheckConstraint("CK_ticket_reply_documents_annexes", "jsonb_typeof(annexes) = 'array' AND jsonb_array_length(annexes) <= 10");
                    table.CheckConstraint("CK_ticket_reply_documents_size", "size_bytes > 0");
                    table.ForeignKey(
                        name: "FK_ticket_reply_documents_ticket_replies_reply_id",
                        column: x => x.reply_id,
                        principalSchema: "support",
                        principalTable: "ticket_replies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_reply_documents_signer",
                schema: "support",
                table: "ticket_reply_documents",
                column: "signer_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_reply_documents_tenant_ticket",
                schema: "support",
                table: "ticket_reply_documents",
                columns: new[] { "tenant_id", "ticket_id" });
        }

        /// <inheritdoc />
        /// <remarks>Fase 4b (H9): la tabla es prueba legal de las respuestas formales enviadas. Este <c>Down</c> nunca se corre en
        /// producción: el rollback es revertir el código y dejar la tabla.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_reply_documents",
                schema: "support");
        }
    }
}
