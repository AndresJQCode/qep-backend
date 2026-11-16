using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Support.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportLegalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_acceptances",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_html = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    authorization_question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    characterization_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    visitor_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_acceptances", x => x.id);
                    table.CheckConstraint("CK_legal_acceptances_ip", "(kind <> 'PrivacyNotice' OR visitor_ip IS NOT NULL) AND (kind <> 'AnonymousNotice' OR visitor_ip IS NULL)");
                    table.CheckConstraint("CK_legal_acceptances_kind", "kind IN ('PrivacyNotice', 'AnonymousNotice', 'Terms')");
                });

            migrationBuilder.CreateTable(
                name: "legal_document_versions",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    content_html = table.Column<string>(type: "character varying(60000)", maxLength: 60000, nullable: false),
                    authorization_question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    policy_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_document_versions", x => x.id);
                    table.CheckConstraint("CK_legal_document_versions_kind", "kind IN ('PrivacyNotice', 'AnonymousNotice', 'Terms')");
                    table.CheckConstraint("CK_legal_document_versions_privacy_only", "kind = 'PrivacyNotice' OR (authorization_question IS NULL AND policy_url IS NULL)");
                    table.CheckConstraint("CK_legal_document_versions_version", "version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_legal_acceptances_ticket_kind",
                schema: "support",
                table: "legal_acceptances",
                columns: new[] { "ticket_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legal_document_versions_created_by",
                schema: "support",
                table: "legal_document_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_legal_document_versions_tenant_kind_version",
                schema: "support",
                table: "legal_document_versions",
                columns: new[] { "tenant_id", "kind", "version" },
                unique: true);

            // Spec 2026-11-15 §11 (plan L12): each tenant's current phase 3 legal text becomes PrivacyNotice version 1 (authorization and,
            // if any, policy text; the URL is copied, the question stays null so the default one applies) and, only if it had terms,
            // Terms version 1, with the author and date of the source version. No AnonymousNotice: there is no tenant text to take it
            // from. The conversion gives the sanitizer's canonical form (BackfilledHtmlIsAlreadyCanonical), so GET /form serves these
            // bytes unchanged. A text with nothing but whitespace is not copied: it would be a document without visible text.
            //
            // Escaping can grow a text up to five times (& is &amp;) and the column holds 60 000. The migration stops with the tenant's
            // id rather than truncate a legal text: that tenant's text is shortened by hand and the deploy runs again.
            migrationBuilder.Sql($@"
DO $$
DECLARE
    offending uuid;
BEGIN
    SELECT current_text.tenant_id INTO offending
    FROM ({CurrentText}) AS current_text
    WHERE char_length({PrivacyContent}) > 60000
       OR char_length({Paragraphs("current_text.terms_text")}) > 60000
    LIMIT 1;
    IF offending IS NOT NULL THEN
        RAISE EXCEPTION 'tenant % legal text exceeds 60000 characters after escaping', offending;
    END IF;
END $$;");

            migrationBuilder.Sql($@"
WITH current_text AS ({CurrentText})
INSERT INTO support.legal_document_versions
    (id, tenant_id, kind, version, content_html, authorization_question, policy_url, created_at, created_by)
SELECT gen_random_uuid(), current_text.tenant_id, 'PrivacyNotice', 1,
       {PrivacyContent},
       NULL, current_text.policy_url, current_text.created_at, current_text.created_by
FROM current_text
WHERE {HasText("coalesce(current_text.authorization_text, '') || coalesce(current_text.policy_text, '')")};");

            migrationBuilder.Sql($@"
WITH current_text AS ({CurrentText})
INSERT INTO support.legal_document_versions
    (id, tenant_id, kind, version, content_html, authorization_question, policy_url, created_at, created_by)
SELECT gen_random_uuid(), current_text.tenant_id, 'Terms', 1,
       {Paragraphs("current_text.terms_text")},
       NULL, NULL, current_text.created_at, current_text.created_by
FROM current_text
WHERE current_text.terms_text IS NOT NULL AND {HasText("current_text.terms_text")};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // `legal_document_versions` and `legal_acceptances` are legal evidence: this Down drops their indexes and keeps the tables
            // and their rows (plan L11; precedent `AddSupportPublicForm` and `customers.data_consents`). Running this migration up again
            // needs them reviewed by hand first, because its CreateTable would collide with them. Never run in production: the
            // rollback is reverting the code.
            migrationBuilder.DropIndex(
                name: "IX_legal_acceptances_ticket_kind",
                schema: "support",
                table: "legal_acceptances");

            migrationBuilder.DropIndex(
                name: "IX_legal_document_versions_created_by",
                schema: "support",
                table: "legal_document_versions");

            migrationBuilder.DropIndex(
                name: "IX_legal_document_versions_tenant_kind_version",
                schema: "support",
                table: "legal_document_versions");
        }

        // Each tenant's phase 3 text with the highest version: the one that rules today.
        private const string CurrentText = @"
    SELECT DISTINCT ON (tenant_id) tenant_id, authorization_text, policy_text, policy_url, terms_text, created_at, created_by
    FROM support.legal_text_versions
    ORDER BY tenant_id, version DESC";

        private static string PrivacyContent =>
            Paragraphs("current_text.authorization_text") + " || " + Paragraphs("current_text.policy_text");

        // Something other than spaces, tabs, line breaks and no-break spaces.
        private static string HasText(string expression) => $@"btrim({expression}, E' \t\r\n' || chr(160)) <> ''";

        // Plan L12: a plain text as canonical HTML, or '' for NULL. CRLF and then a lone CR become LF; only &, <, > (in that order) and
        // the no-break space are escaped, which is what the serializer escapes in text; the text is split on a line break followed by
        // blank lines; each part is trimmed and empty ones dropped; inside a part a line break becomes <br>; each part is wrapped in <p>.
        private static string Paragraphs(string column) => $@"
       (SELECT coalesce(string_agg('<p>' || replace(btrim(part.chunk, E' \t\r\n'), E'\n', '<br>') || '</p>', '' ORDER BY part.ord), '')
        FROM regexp_split_to_table(
            replace(replace(replace(replace(replace(replace({column}, E'\r\n', E'\n'), E'\r', E'\n'), '&', '&amp;'), chr(160), '&nbsp;'), '<', '&lt;'), '>', '&gt;'),
            E'\n\\s*\n') WITH ORDINALITY AS part(chunk, ord)
        WHERE btrim(part.chunk, E' \t\r\n') <> '')";
    }
}
