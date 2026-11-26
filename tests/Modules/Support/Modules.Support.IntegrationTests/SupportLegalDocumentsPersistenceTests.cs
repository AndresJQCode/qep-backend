using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The schema of the legal documents (migration <c>AddSupportLegalDocuments</c>, spec 2026-11-15 §5.1, §5.2 and §5.4): the versions per
/// kind, the acceptances without FK, their checks and the phase 3 tables that stay mapped without a writer. The shared host is used by
/// the tests that only touch their own tenant; the migration one starts its own database (it migrates down).
/// </summary>
public sealed class SupportLegalDocumentsPersistenceTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private const string Ip = "203.0.113.7";

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoVersionsWithTheSameNumberAndKindAre409()
    {
        var (factory, tenant) = await NewTenantAsync();
        await SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.Terms, 1);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.Terms, 1));

        Assert.Equal(SupportErrorCodes.LegalDocumentsVersionTaken, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var documents = read.ServiceProvider.GetRequiredService<ISupportLegalDocumentRepository>();
        Assert.Single(await documents.ListAsync(tenant, SupportLegalDocumentKind.Terms, Ct));
    }

    [Fact]
    public async Task TheSameNumberInAnotherKindIsFine()
    {
        var (factory, tenant) = await NewTenantAsync();
        var terms = await SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.Terms, 1);
        var notice = await SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.AnonymousNotice, 1);
        await SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.Terms, 2);

        await using var read = factory.Services.CreateAsyncScope();
        var documents = read.ServiceProvider.GetRequiredService<ISupportLegalDocumentRepository>();
        var current = await documents.CurrentAllAsync(tenant, Ct);

        Assert.Equal(2, current[SupportLegalDocumentKind.Terms].Version);
        Assert.Equal(notice.Id, current[SupportLegalDocumentKind.AnonymousNotice].Id);
        Assert.False(current.ContainsKey(SupportLegalDocumentKind.PrivacyNotice));
        Assert.Equal([2, 1], (await documents.ListAsync(tenant, SupportLegalDocumentKind.Terms, Ct)).Select(item => item.Version));
        Assert.Equal(terms.Id, (await documents.FindAsync(tenant, SupportLegalDocumentKind.Terms, 1, Ct))!.Id);
        Assert.Null(await documents.FindAsync(tenant, SupportLegalDocumentKind.Terms, 3, Ct));
        Assert.Null(await documents.CurrentAsync(Guid.CreateVersion7(), SupportLegalDocumentKind.Terms, Ct));
    }

    [Fact]
    public async Task ACheckRejectsAQuestionOnTerms()
    {
        var (factory, tenant) = await NewTenantAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            factory,
            """
            INSERT INTO support.legal_document_versions
                (id, tenant_id, kind, version, content_html, authorization_question, policy_url, created_at, created_by)
            VALUES (gen_random_uuid(), @tenant, 'Terms', 1, '<p>a</p>', '¿Autorizas?', NULL, now(), gen_random_uuid())
            """,
            ("tenant", tenant)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_legal_document_versions_privacy_only", exception.ConstraintName);
    }

    [Theory]
    [InlineData("PrivacyNotice", null)]
    [InlineData("AnonymousNotice", Ip)]
    public async Task TheIpCheckRejectsAPrivacyRowWithoutIpAndAnAnonymousNoticeWithIp(string kind, string? ip)
    {
        var (factory, tenant) = await NewTenantAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            factory,
            """
            INSERT INTO support.legal_acceptances
                (id, tenant_id, ticket_id, kind, document_version_id, document_version, accepted_at, content_html, visitor_ip)
            VALUES (gen_random_uuid(), @tenant, gen_random_uuid(), @kind, gen_random_uuid(), 1, now(), '<p>a</p>', @ip)
            """,
            ("tenant", tenant), ("kind", kind), ("ip", (object?)ip ?? DBNull.Value)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_legal_acceptances_ip", exception.ConstraintName);
    }

    [Fact]
    public async Task ASecondAcceptanceOfTheSameKindIs409()
    {
        var (factory, tenant) = await NewTenantAsync();
        var terms = await SaveVersionAsync(factory, tenant, SupportLegalDocumentKind.Terms, 1);
        var ticket = SupportTicketId.New();
        await AddAcceptanceAsync(factory, tenant, ticket, terms);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => AddAcceptanceAsync(factory, tenant, ticket, terms));

        Assert.Equal(SupportErrorCodes.PublicLegalAcceptanceDuplicate, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var acceptances = read.ServiceProvider.GetRequiredService<ISupportLegalAcceptanceRepository>();
        var summary = Assert.Single(await acceptances.ListByTicketAsync(tenant, ticket, Ct));
        Assert.Equal(SupportLegalDocumentKind.Terms, summary.Kind);
        Assert.Equal(Ip, summary.VisitorIp);
        Assert.Equal("<p>Servido 1.</p>", (await acceptances.FindAsync(tenant, ticket, SupportLegalDocumentKind.Terms, Ct))!.ContentHtml);
        Assert.Null(await acceptances.FindAsync(tenant, ticket, SupportLegalDocumentKind.PrivacyNotice, Ct));
        Assert.Empty(await acceptances.ListByTicketAsync(Guid.CreateVersion7(), ticket, Ct));
    }

    [Fact]
    public async Task TheAcceptancesHaveNoForeignKey()
    {
        var factory = await sharedHost.StartTestAsync();

        foreach (var table in new[] { "legal_acceptances", "legal_document_versions" })
        {
            Assert.Equal(1L, await ScalarAsync(
                factory,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'support' AND table_name = @table",
                ("table", table)));
            Assert.Equal(0L, await ScalarAsync(
                factory,
                """
                SELECT count(*) FROM information_schema.table_constraints
                WHERE table_schema = 'support' AND table_name = @table AND constraint_type = 'FOREIGN KEY'
                """,
                ("table", table)));
        }
    }

    // Plan L11: the Down drops the indexes of the two new tables and keeps the tables (legal evidence, like AddSupportPublicForm).
    [Fact]
    public async Task TheMigrationDownKeepsTheLegalDocumentTables()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var factory = new QepApiFactory(connectionString, RemoveThreadFollowWorker, WorkersOff))
        {
            _ = factory.Services;
        }

        await using var context = SupportContext(connectionString);
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(id => id.EndsWith("_AddSupportLegalDocuments", StringComparison.Ordinal));
        Assert.True(index > 0);

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1], Ct);

        Assert.Equal(
            ["legal_acceptances", "legal_document_versions"],
            await NamesAsync(
                context,
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'support' AND table_name = ANY(@names) ORDER BY 1",
                ["legal_acceptances", "legal_document_versions"]));
        Assert.Empty(await NamesAsync(
            context,
            "SELECT indexname::text FROM pg_indexes WHERE schemaname = 'support' AND indexname = ANY(@names) ORDER BY 1",
            [SupportDbContext.LegalDocumentVersionIndex, SupportDbContext.LegalAcceptanceTicketKindIndex]));
    }

    // Spec §5.4 (plan L10): the phase 3 tables keep their rows and stay in the EF model. Taking them out would make the next migration
    // drop them.
    [Fact]
    public async Task TheLegacyTablesAreStillThere()
    {
        var factory = await sharedHost.StartTestAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var model = scope.ServiceProvider.GetRequiredService<SupportDbContext>().Model;

        foreach (var table in new[] { "consents", "legal_text_versions" })
        {
            Assert.Equal(1L, await ScalarAsync(
                factory,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'support' AND table_name = @table",
                ("table", table)));
        }

        Assert.Equal("legal_text_versions", model.FindEntityType(typeof(SupportLegalTextVersion))!.GetTableName());
        Assert.Equal("consents", model.FindEntityType(typeof(SupportConsent))!.GetTableName());
    }

    private async Task<(QepApiFactory Factory, Guid Tenant)> NewTenantAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenantId = NewTenantId();
        await SeedTenantAsync(factory, tenantId);
        return (factory, Guid.Parse(tenantId));
    }

    private static async Task<SupportLegalDocumentVersion> SaveVersionAsync(
        QepApiFactory factory, Guid tenant, SupportLegalDocumentKind kind, int version)
    {
        var document = SupportLegalDocumentVersion.Create(
            Guid.CreateVersion7(), tenant, kind, version, new SanitizedHtml($"<p>Versión {version}.</p>", true), null, null,
            Guid.CreateVersion7(), Now);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportLegalDocumentRepository>().Add(document);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return document;
    }

    private static async Task AddAcceptanceAsync(
        QepApiFactory factory, Guid tenant, SupportTicketId ticket, SupportLegalDocumentVersion version)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportLegalAcceptanceRepository>().Add(SupportLegalAcceptance.Record(
            Guid.CreateVersion7(), tenant, ticket, version, $"<p>Servido {version.Version}.</p>", null, null, Ip, Now));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static SupportDbContext SupportContext(string connectionString) =>
        new(new DbContextOptionsBuilder<SupportDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SupportDbContext.Schema))
            .Options);

    private static async Task<List<string>> NamesAsync(SupportDbContext context, string sql, string[] names)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "names";
        parameter.Value = names;
        command.Parameters.Add(parameter);
        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }
}
