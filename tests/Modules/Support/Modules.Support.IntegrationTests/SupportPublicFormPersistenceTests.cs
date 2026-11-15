using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El esquema de la fase 3 (migración <c>AddSupportPublicForm</c>, plan F7, F14, F16 y F17): la clave de consulta, las versiones del
/// formulario y de los textos legales, el consentimiento sin FK y la foto del solicitante con ciudad. Comparten host las que sólo leen
/// o escriben su tenant; la de la migración levanta su propia base (la baja).
/// </summary>
public sealed class SupportPublicFormPersistenceTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheConsentsTableHasNoForeignKey()
    {
        var factory = await sharedHost.StartTestAsync();

        var foreignKeys = await ScalarAsync(
            factory,
            """
            SELECT count(*) FROM information_schema.table_constraints
            WHERE table_schema = 'support' AND table_name = 'consents' AND constraint_type = 'FOREIGN KEY'
            """);

        Assert.Equal(1L, await ScalarAsync(
            factory,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'support' AND table_name = 'consents'"));
        Assert.Equal(0L, foreignKeys);
    }

    [Fact]
    public async Task TheMigrationDownKeepsTheLegalTables()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var factory = new QepApiFactory(connectionString, RemoveThreadFollowWorker, WorkersOff))
        {
            _ = factory.Services;
        }

        await using var context = new SupportDbContext(
            new DbContextOptionsBuilder<SupportDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SupportDbContext.Schema))
                .Options);
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(id => id.EndsWith("_AddSupportPublicForm", StringComparison.Ordinal));
        Assert.True(index > 0);

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1], Ct);

        Assert.Equal(
            ["consents", "legal_text_versions"],
            await TablesAsync(context, ["consents", "legal_text_versions", "form_versions", "ticket_access"]));
    }

    [Fact]
    public async Task ARequesterWithCityRoundTrips()
    {
        var (factory, tenant) = await NewTenantAsync();
        var city = Guid.CreateVersion7();
        var colombian = new TicketRequester(
            "Ana", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", "601 555 0101", "CO", "Calle 1", city, null);
        var spanish = colombian with { CountryCode = "ES", CityId = null, CityName = "Valencia", DocumentType = "NIF" };
        var form = await SaveFormAsync(factory, tenant, 1);
        var first = await FromFormAsync(factory, tenant, 1, colombian, form.Id);
        var second = await FromFormAsync(factory, tenant, 2, spanish, form.Id);

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var foundFirst = await tickets.FindAsync(tenant, first.Id, Ct);
        var foundSecond = await tickets.FindAsync(tenant, second.Id, Ct);

        Assert.Equal(colombian, foundFirst!.Requester);
        Assert.Equal(spanish, foundSecond!.Requester);
        Assert.Equal(SupportTicketIntake.PublicForm, foundFirst.Intake);
        Assert.Equal(form.Id, foundFirst.FormVersionId);
        Assert.False(foundFirst.IsAnonymous);
        Assert.Equal(SupportReplyChannel.Email, foundFirst.ReplyChannel);
    }

    [Fact]
    public async Task AnAnonymousTicketRoundTripsWithoutRequester()
    {
        var (factory, tenant) = await NewTenantAsync();
        var form = await SaveFormAsync(factory, tenant, 1);
        var ticket = await FromFormAsync(factory, tenant, 1, requester: null, form.Id);

        await using var read = factory.Services.CreateAsyncScope();
        var found = await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);

        Assert.True(found!.IsAnonymous);
        Assert.Null(found.Requester);
        Assert.Null(found.CustomerId);
        Assert.Equal(SupportReplyChannel.Portal, found.ReplyChannel);
    }

    [Fact]
    public async Task TheReplyChannelCheckAcceptsPortal()
    {
        var (factory, tenant) = await NewTenantAsync();
        var form = await SaveFormAsync(factory, tenant, 1);
        var ticket = await FromFormAsync(factory, tenant, 1, requester: null, form.Id);
        var reply = SupportTicketReply.Start(
            tenant, ticket.Id, Guid.CreateVersion7(), SupportReplyChannel.Portal, SupportReplyDelivery.Portal,
            "Ya revisamos tu caso.", null, Guid.CreateVersion7(), Now.AddMinutes(5));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>().Add(reply);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var found = Assert.Single(await read.ServiceProvider.GetRequiredService<ISupportReplyRepository>().ListAsync(tenant, ticket.Id, Ct));
        Assert.Equal(SupportReplyChannel.Portal, found.Channel);
        Assert.Equal(SupportReplyDelivery.Portal, found.Delivery);
    }

    [Fact]
    public async Task TwoFormVersionsWithTheSameNumberAre409()
    {
        var (factory, tenant) = await NewTenantAsync();
        await SaveFormAsync(factory, tenant, 1);
        await SaveFormAsync(factory, tenant, 2);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveFormAsync(factory, tenant, 2));

        Assert.Equal(SupportErrorCodes.FormVersionTaken, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var forms = read.ServiceProvider.GetRequiredService<ISupportFormRepository>();
        var current = await forms.CurrentAsync(tenant, Ct);
        Assert.Equal(2, current!.Version);
        Assert.Null(await forms.CurrentAsync(Guid.CreateVersion7(), Ct));
        Assert.NotNull(await forms.FindAsync(tenant, current.Id, Ct));
        Assert.Null(await forms.FindAsync(Guid.CreateVersion7(), current.Id, Ct));
    }

    [Fact]
    public async Task TwoLegalTextVersionsWithTheSameNumberAre409()
    {
        var (factory, tenant) = await NewTenantAsync();
        await SaveLegalTextAsync(factory, tenant, 1);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveLegalTextAsync(factory, tenant, 1));

        Assert.Equal(SupportErrorCodes.LegalTextsVersionTaken, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        var texts = read.ServiceProvider.GetRequiredService<ISupportLegalTextRepository>();
        var current = await texts.CurrentAsync(tenant, Ct);
        Assert.Equal(1, current!.Version);
        Assert.NotNull(await texts.FindAsync(tenant, current.Id, Ct));
    }

    [Fact]
    public async Task TheConsentKeepsItsOwnCopyOfTheText()
    {
        var (factory, tenant) = await NewTenantAsync();
        var form = await SaveFormAsync(factory, tenant, 1);
        var text = await SaveLegalTextAsync(factory, tenant, 1);
        var ticket = await FromFormAsync(
            factory, tenant, 1,
            new TicketRequester("Ana", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null, Guid.CreateVersion7(), null),
            form.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportConsentRepository>()
                .Add(SupportConsent.Record(Guid.CreateVersion7(), tenant, ticket.Id, text, "203.0.113.7", Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await SaveLegalTextAsync(factory, tenant, 2);

        Assert.Equal(text.AuthorizationText, await TextAsync(
            factory, "SELECT authorization_text FROM support.consents WHERE ticket_id = @ticket", ("ticket", ticket.Id.Value)));
        Assert.Equal("203.0.113.7", await TextAsync(
            factory, "SELECT visitor_ip FROM support.consents WHERE ticket_id = @ticket", ("ticket", ticket.Id.Value)));
    }

    [Fact]
    public async Task TheTicketAccessRoundTripsAndIsFoundByNumber()
    {
        var (factory, tenant) = await NewTenantAsync();
        var form = await SaveFormAsync(factory, tenant, 1);
        var ticket = await FromFormAsync(factory, tenant, 7, requester: null, form.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var access = SupportTicketAccess.Issue(ticket.Id, tenant, "pin-hash", Now);
            access.RecordFailure(Now.AddMinutes(1));
            access.RecordDelivery("None", "anonymous", Now.AddMinutes(2));
            scope.ServiceProvider.GetRequiredService<ISupportTicketAccessRepository>().Add(access);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var accesses = read.ServiceProvider.GetRequiredService<ISupportTicketAccessRepository>();
        var byId = await accesses.FindAsync(tenant, ticket.Id, Ct);
        var byNumber = await accesses.FindByNumberAsync(tenant, 7, Ct);

        Assert.Equal("pin-hash", byId!.PinHash);
        Assert.Equal(1, byId.FailedAttempts);
        Assert.Equal("None", byId.LastSentChannel);
        Assert.Equal("anonymous", byId.LastFailureCode);
        Assert.Equal(ticket.Id, byNumber!.Value.Ticket.Id);
        Assert.Equal(ticket.Id, byNumber.Value.Access.TicketId);
        Assert.Null(await accesses.FindByNumberAsync(tenant, 8, Ct));
        Assert.Null(await accesses.FindByNumberAsync(Guid.CreateVersion7(), 7, Ct));
    }

    // Revisión del Bloque B (Important 1): la consulta y el reenvío leen la clave con FOR UPDATE, dentro de una transacción; sin ella,
    // el candado de fila no serviría de nada y la lectura lanza.
    [Fact]
    public async Task LockByNumberNeedsATransactionAndLoadsTheAccess()
    {
        var (factory, tenant) = await NewTenantAsync();
        var form = await SaveFormAsync(factory, tenant, 1);
        var ticket = await FromFormAsync(factory, tenant, 7, requester: null, form.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketAccessRepository>()
                .Add(SupportTicketAccess.Issue(ticket.Id, tenant, "pin-hash", Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var accesses = read.ServiceProvider.GetRequiredService<ISupportTicketAccessRepository>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => accesses.LockByNumberAsync(tenant, 7, Ct));
        await using var transaction = await read.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().BeginTransactionAsync(Ct);
        var locked = await accesses.LockByNumberAsync(tenant, 7, Ct);

        Assert.Equal(ticket.Id, locked!.Value.Ticket.Id);
        Assert.Equal("pin-hash", locked.Value.Access.PinHash);
        Assert.Null(await accesses.LockByNumberAsync(tenant, 8, Ct));
    }

    private async Task<(QepApiFactory Factory, Guid Tenant)> NewTenantAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenantId = NewTenantId();
        await SeedTenantAsync(factory, tenantId);
        return (factory, Guid.Parse(tenantId));
    }

    private static async Task<SupportFormVersion> SaveFormAsync(QepApiFactory factory, Guid tenant, int version)
    {
        var form = SupportFormVersion.Create(
            Guid.CreateVersion7(), tenant, version, isPublished: true, showDocument: true, showAddress: true, showLandline: false,
            businessNameRequired: true, "#00B4FF", "Cuéntanos tu caso.", "Recibimos tu caso.", Guid.CreateVersion7(), Now);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportFormRepository>().Add(form);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return form;
    }

    private static async Task<SupportLegalTextVersion> SaveLegalTextAsync(QepApiFactory factory, Guid tenant, int version)
    {
        var text = SupportLegalTextVersion.Create(
            Guid.CreateVersion7(), tenant, version, $"Autorizo el tratamiento de mis datos, versión {version}.", null,
            "https://example.com/politica", null, Guid.CreateVersion7(), Now);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportLegalTextRepository>().Add(text);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return text;
    }

    private static async Task<SupportTicket> FromFormAsync(
        QepApiFactory factory, Guid tenant, long number, TicketRequester? requester, Guid formVersionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var type = (await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct))[0];
        var ticket = SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), tenant, number, type, $"Asunto {number}", "Lo escribió en el formulario.", requester, null,
            formVersionId, Now, Clock);
        scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return ticket;
    }

    private static async Task<List<string>> TablesAsync(SupportDbContext context, string[] names)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'support' AND table_name = ANY(@names) ORDER BY table_name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "names";
        parameter.Value = names;
        command.Parameters.Add(parameter);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
