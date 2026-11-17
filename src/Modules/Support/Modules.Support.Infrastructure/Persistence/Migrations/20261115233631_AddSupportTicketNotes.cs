using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportTicketNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_notes",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_notes", x => x.id);
                    table.ForeignKey(
                        name: "FK_ticket_notes_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_note_files",
                schema: "support",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_note_files", x => new { x.note_id, x.file_id });
                    table.CheckConstraint("CK_ticket_note_files_position", "position BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_ticket_note_files_size", "size_bytes > 0");
                    table.ForeignKey(
                        name: "FK_ticket_note_files_ticket_notes_note_id",
                        column: x => x.note_id,
                        principalSchema: "support",
                        principalTable: "ticket_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_note_files_file",
                schema: "support",
                table: "ticket_note_files",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_notes_author",
                schema: "support",
                table: "ticket_notes",
                column: "author_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_notes_tenant_ticket_created",
                schema: "support",
                table: "ticket_notes",
                columns: new[] { "tenant_id", "ticket_id", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_notes_ticket_id",
                schema: "support",
                table: "ticket_notes",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_note_files",
                schema: "support");

            migrationBuilder.DropTable(
                name: "ticket_notes",
                schema: "support");
        }
    }
}
