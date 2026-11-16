using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Support.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The backfill of <c>AddSupportLegalDocuments</c> (spec 2026-11-15 §11, plan L12): each tenant's current phase 3 legal text becomes
/// <c>PrivacyNotice</c> version 1 and, if it had terms, <c>Terms</c> version 1, converted to canonical HTML; no <c>AnonymousNotice</c>
/// is invented. Its own database: Support alone is migrated up to the migration before, the old texts are seeded by SQL, and the
/// migration under test runs on them.
/// </summary>
public sealed class SupportLegalDocumentsBackfillTests(SupportLegalDocumentsBackfillTests.BackfilledDatabase database)
    : IClassFixture<SupportLegalDocumentsBackfillTests.BackfilledDatabase>
{
    private static readonly Guid TenantWithEverything = Guid.Parse("0193a000-0000-7000-8000-00000000c001");
    private static readonly Guid TenantWithPolicyText = Guid.Parse("0193a000-0000-7000-8000-00000000c002");
    private static readonly Guid TenantWithoutTexts = Guid.Parse("0193a000-0000-7000-8000-00000000c003");
    private static readonly Guid FirstAuthor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid CurrentAuthor = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset FirstCreatedAt = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentCreatedAt = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCurrentLegacyVersionBecomesPrivacyNoticeOne()
    {
        var rows = await database.DocumentsAsync(TenantWithEverything, "PrivacyNotice");

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Version);
        Assert.Equal(
            "<p>Autorizo a A &amp; B &lt;socios&gt; a tratar mis datos.</p><p>Segundo párrafo<br>con salto.</p>"
            + "<p>Política \"oficial\" de datos.</p>",
            row.ContentHtml);
        Assert.Equal("https://a.example.com/politica", row.PolicyUrl);
        Assert.Null(row.AuthorizationQuestion);
        Assert.Equal(CurrentAuthor, row.CreatedBy);
        Assert.Equal(CurrentCreatedAt, row.CreatedAt);

        var policyOnly = Assert.Single(await database.DocumentsAsync(TenantWithPolicyText, "PrivacyNotice"));
        Assert.Equal("<p>Autorizo.</p><p>Política propia.</p>", policyOnly.ContentHtml);
        Assert.Null(policyOnly.PolicyUrl);
        Assert.Equal(FirstAuthor, policyOnly.CreatedBy);
    }

    [Fact]
    public async Task TermsAreCopiedOnlyWhenTheyExisted()
    {
        var terms = Assert.Single(await database.DocumentsAsync(TenantWithEverything, "Terms"));

        Assert.Equal(1, terms.Version);
        Assert.Equal("<p>Términos uno.</p><p>Términos dos.</p>", terms.ContentHtml);
        Assert.Null(terms.PolicyUrl);
        Assert.Equal(CurrentAuthor, terms.CreatedBy);
        Assert.Empty(await database.DocumentsAsync(TenantWithPolicyText, "Terms"));
    }

    [Fact]
    public async Task NoAnonymousNoticeIsCreated()
    {
        Assert.Empty(await database.DocumentsAsync(TenantWithEverything, "AnonymousNotice"));
        Assert.Empty(await database.DocumentsAsync(TenantWithPolicyText, "AnonymousNotice"));
    }

    [Fact]
    public async Task ATenantWithoutLegacyTextsGetsNothing()
    {
        foreach (var kind in new[] { "PrivacyNotice", "AnonymousNotice", "Terms" })
        {
            Assert.Empty(await database.DocumentsAsync(TenantWithoutTexts, kind));
        }

        // The legacy rows are untouched.
        Assert.Equal(3L, await database.ScalarAsync("SELECT count(*) FROM support.legal_text_versions"));
    }

    public sealed record DocumentRow(
        int Version, string ContentHtml, string? AuthorizationQuestion, string? PolicyUrl, DateTimeOffset CreatedAt, Guid CreatedBy);

    /// <summary>One container for the class: up to <c>AddSupportTicketNotes</c>, the phase 3 texts, then <c>AddSupportLegalDocuments</c>.</summary>
    public sealed class BackfilledDatabase : IAsyncLifetime
    {
        private PostgreSqlContainer? _container;

        private string ConnectionString =>
            _container?.GetConnectionString() ?? throw new InvalidOperationException("The database has not been started.");

        public async ValueTask InitializeAsync()
        {
            _container = await SupportApiHarness.StartDatabaseAsync();
            await using var context = new SupportDbContext(
                new DbContextOptionsBuilder<SupportDbContext>()
                    .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SupportDbContext.Schema))
                    .Options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("AddSupportTicketNotes", Ct);
            await SeedLegacyTextsAsync();
            await migrator.MigrateAsync("AddSupportLegalDocuments", Ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }

        public async Task<List<DocumentRow>> DocumentsAsync(Guid tenant, string kind)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                """
                SELECT version, content_html, authorization_question, policy_url, created_at, created_by
                FROM support.legal_document_versions WHERE tenant_id = @tenant AND kind = @kind ORDER BY version
                """,
                connection);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("kind", kind);
            var rows = new List<DocumentRow>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(new DocumentRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetGuid(5)));
            }

            return rows;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        // Tenant 1: v1 (replaced) and v2 (current) with authorization, policy text and URL, and terms; &, <, >, quotes, \r\n and blank
        // lines. Tenant 2: only v1, with a policy text and no terms. Tenant 3: nothing.
        private async Task SeedLegacyTextsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await InsertAsync(
                connection, TenantWithEverything, 1, "Texto viejo que ya no rige.", null, "https://a.example.com/vieja", "Términos viejos.",
                FirstAuthor, FirstCreatedAt);
            await InsertAsync(
                connection, TenantWithEverything, 2,
                "Autorizo a A & B <socios> a tratar mis datos.\r\n\r\nSegundo párrafo\r\ncon salto.",
                "Política \"oficial\" de datos.",
                "https://a.example.com/politica",
                "Términos uno.\n\n\nTérminos dos.",
                CurrentAuthor, CurrentCreatedAt);
            await InsertAsync(
                connection, TenantWithPolicyText, 1, "Autorizo.", "Política propia.", null, null, FirstAuthor, FirstCreatedAt);
        }

        private static async Task InsertAsync(
            NpgsqlConnection connection, Guid tenant, int version, string authorization, string? policyText, string? policyUrl,
            string? termsText, Guid author, DateTimeOffset createdAt)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO support.legal_text_versions
                    (id, tenant_id, version, authorization_text, policy_text, policy_url, terms_text, created_at, created_by)
                VALUES (gen_random_uuid(), @tenant, @version, @authorization, @policyText, @policyUrl, @termsText, @createdAt, @author)
                """,
                connection);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("version", version);
            command.Parameters.AddWithValue("authorization", authorization);
            command.Parameters.AddWithValue("policyText", (object?)policyText ?? DBNull.Value);
            command.Parameters.AddWithValue("policyUrl", (object?)policyUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("termsText", (object?)termsText ?? DBNull.Value);
            command.Parameters.AddWithValue("createdAt", createdAt);
            command.Parameters.AddWithValue("author", author);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
