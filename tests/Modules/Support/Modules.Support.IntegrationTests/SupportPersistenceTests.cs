using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El esquema <c>support</c>: la siembra perezosa de los tipos, el radicado por tenant, el umbral de «por vencer», el ida y
/// vuelta del ticket y la traducción del nombre de tipo repetido. Comparten host: cada prueba siembra su propio tenant y sólo
/// lee lo de ese tenant.
/// </summary>
public sealed class SupportPersistenceTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] DefaultNames =
    [
        "Petición general", "Petición de documentos o información", "Consulta", "Queja", "Reclamo", "Sugerencia",
        "Felicitación", "Soporte técnico",
    ];

    [Fact]
    public async Task TheFirstAccessSeedsTheEightDefaultTypesInOrder()
    {
        var (factory, tenant) = await NewTenantAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var types = await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct);

        Assert.Equal(DefaultNames, types.Select(type => type.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], types.Select(type => type.Position));
        Assert.Equal([15, 10, 30, 15, 15, 15, 15, null], types.Select(type => type.TermBusinessDays));
        Assert.Equal([true, true, true, true, true, true, true, false], types.Select(type => type.TermIsLegal));
        Assert.Equal([true, true, true, true, true, false, false, false], types.Select(type => type.RequiresFormalResponse));
        Assert.All(types, type =>
        {
            Assert.Equal(tenant, type.TenantId);
            Assert.True(type.IsActive);
            Assert.True(type.IsPublished);
            Assert.Equal(1L, type.Version);
        });
    }

    [Fact]
    public async Task ASecondAccessDoesNotSeedAgain()
    {
        var (factory, tenant) = await NewTenantAsync();

        await EnsureAsync(factory, tenant);
        await EnsureAsync(factory, tenant);

        Assert.Equal(DefaultNames, (await TypesAsync(factory, tenant)).Select(type => type.Name));
    }

    [Fact]
    public async Task TwoFirstAccessesSeedTheTypesOnce()
    {
        var (factory, tenant) = await NewTenantAsync();

        await Task.WhenAll(EnsureAsync(factory, tenant), EnsureAsync(factory, tenant));

        var types = await TypesAsync(factory, tenant);
        Assert.Equal(8, types.Count);
        Assert.Equal(DefaultNames, types.Select(type => type.Name));
    }

    [Fact]
    public async Task NumbersAreConsecutivePerTenant()
    {
        var (factory, tenantA) = await NewTenantAsync();
        var (_, tenantB) = await NewTenantAsync();
        await EnsureAsync(factory, tenantA);
        await EnsureAsync(factory, tenantB);

        Assert.Equal(1L, await NextCommittedAsync(factory, tenantA));
        Assert.Equal(2L, await NextCommittedAsync(factory, tenantA));
        Assert.Equal(1L, await NextCommittedAsync(factory, tenantB));
    }

    [Fact]
    public async Task ARolledBackCreationDoesNotKeepItsNumber()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var transaction = await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().BeginTransactionAsync(Ct);
            Assert.Equal(1L, await scope.ServiceProvider.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenant, Ct));
        }

        Assert.Equal(1L, await NextCommittedAsync(factory, tenant));
        Assert.Equal(2L, await NextCommittedAsync(factory, tenant));
    }

    [Fact]
    public async Task TheNumberNeedsAnOpenTransaction()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using var scope = factory.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenant, Ct));
    }

    [Fact]
    public async Task ATicketRoundTripsWithItsRequesterAndEvents()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[3];
        var requester = new TicketRequester(
            "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", "6012345678", "CO", "Calle 1 # 2-3");
        var actor = Guid.CreateVersion7();
        var assignee = Guid.CreateVersion7();
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 41, type, "No me llegó la factura", "Pedí la factura y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Letter, SupportReplyChannel.WhatsAppTemplate, null, requester, assignee,
            actor, Now, Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, actor, Now.AddDays(1), Clock);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var found = await tickets.FindAsync(tenant, ticket.Id, Ct);
        var events = await tickets.ListEventsAsync(tenant, ticket.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(41L, found.Number);
        Assert.Equal("SOP-000041", found.DisplayNumber);
        Assert.Equal(type.Id, found.TypeId);
        Assert.Equal("Queja", found.TypeName);
        Assert.Equal(15, found.TermBusinessDays);
        Assert.True(found.TermIsLegal);
        Assert.True(found.RequiresFormalResponse);
        Assert.Equal("No me llegó la factura", found.Subject);
        Assert.Equal("Pedí la factura y no llegó.", found.Description);
        Assert.Equal(SupportTicketIntake.Manual, found.Intake);
        Assert.Equal(ManualIntakeMedium.Letter, found.ManualMedium);
        Assert.Equal(SupportReplyChannel.WhatsAppTemplate, found.ReplyChannel);
        Assert.Equal(SupportTicketStatus.WaitingOnCustomer, found.Status);
        Assert.Equal(assignee, found.AssigneeMemberId);
        Assert.Null(found.CustomerId);
        Assert.Equal(requester, found.Requester);
        Assert.Equal(Now, found.ReceivedAt);
        Assert.Equal(new DateOnly(2026, 11, 13), found.ReceivedOn);
        Assert.Equal(new DateOnly(2026, 12, 4), found.DueOn);
        Assert.Equal(Now.AddDays(1), found.PausedSince);
        Assert.Equal(0, found.PausedBusinessDays);
        Assert.Equal(0, found.ResolutionCount);
        Assert.Equal(2, found.LastEventOrdinal);
        Assert.Equal(2L, found.Version);
        Assert.Empty(found.Events);
        Assert.Equal([1, 2], events.Select(item => item.Ordinal));
        Assert.Equal([SupportTicketEventKind.Created, SupportTicketEventKind.StatusChanged], events.Select(item => item.Kind));
        Assert.All(events, item => Assert.Equal(actor, item.ActorMemberId));
        Assert.Equal(Now.AddDays(1), events[1].OccurredAt);
        using var details = JsonDocument.Parse(events[1].Details);
        Assert.Equal("New", details.RootElement.GetProperty("from").GetString());
        Assert.Equal("WaitingOnCustomer", details.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task AChangeToALoadedTicketAppendsItsEvent()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[0];
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, type, 1, Now, actor);
        await SaveAsync(factory, ticket);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
            loaded!.ChangeStatus(SupportTicketStatus.Open, actor, Now.AddHours(1), Clock);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        Assert.Equal(SupportTicketStatus.Open, (await tickets.FindAsync(tenant, ticket.Id, Ct))!.Status);
        Assert.Equal([1, 2], (await tickets.ListEventsAsync(tenant, ticket.Id, Ct)).Select(item => item.Ordinal));
    }

    // Spec 2026-11-15 §5.4: la prioridad va por nombre y el CHECK de la traza admite PriorityChanged.
    [Fact]
    public async Task ATicketPriorityAndItsChangeEventRoundTrip()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[0];
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, type, 1, Now, actor, SupportTicketPriority.High);
        await SaveAsync(factory, ticket);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
            Assert.Equal(SupportTicketPriority.High, loaded!.Priority);
            loaded.ChangePriority(SupportTicketPriority.Urgent, actor, Now.AddHours(1));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var found = await tickets.FindAsync(tenant, ticket.Id, Ct);
        var events = await tickets.ListEventsAsync(tenant, ticket.Id, Ct);

        Assert.Equal(SupportTicketPriority.Urgent, found!.Priority);
        Assert.Equal(2L, found.Version);
        Assert.Equal([SupportTicketEventKind.Created, SupportTicketEventKind.PriorityChanged], events.Select(item => item.Kind));
        Assert.Equal(actor, events[1].ActorMemberId);
        using var details = JsonDocument.Parse(events[1].Details);
        Assert.Equal("High", details.RootElement.GetProperty("from").GetString());
        Assert.Equal("Urgent", details.RootElement.GetProperty("to").GetString());
    }

    // Fase 4a (plan G9): las tres columnas de la prórroga y el evento Extended, que el CHECK de kind tiene que aceptar.
    [Fact]
    public async Task AnExtendedTicketRoundTrips()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[0];
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, type, 1, Now, actor);
        await SaveAsync(factory, ticket);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
            loaded!.Extend(5, "Falta respuesta del proveedor", actor, At(11, 17), Clock);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var found = await tickets.FindAsync(tenant, ticket.Id, Ct);
        var events = await tickets.ListEventsAsync(tenant, ticket.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(5, found.ExtensionBusinessDays);
        Assert.Equal("Falta respuesta del proveedor", found.ExtensionReason);
        Assert.Equal(At(11, 17), found.ExtendedAt);
        // 15 + 5 días hábiles de lunes a viernes desde el 2026-11-13.
        Assert.Equal(new DateOnly(2026, 12, 11), found.DueOn);
        Assert.Equal(2L, found.Version);
        var extended = events[^1];
        Assert.Equal(SupportTicketEventKind.Extended, extended.Kind);
        Assert.Equal(actor, extended.ActorMemberId);
        using var details = JsonDocument.Parse(extended.Details);
        Assert.Equal("2026-12-04", details.RootElement.GetProperty("fromDueOn").GetString());
        Assert.Equal("2026-12-11", details.RootElement.GetProperty("toDueOn").GetString());
    }

    [Fact]
    public async Task AStaleTicketVersionIsAConcurrencyConflict()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[0];
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, type, 1, Now, actor);
        await SaveAsync(factory, ticket);

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var winner = await first.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
        var loser = await second.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
        winner!.Assign(actor, actor, Now.AddHours(1));
        await first.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        loser!.ChangeStatus(SupportTicketStatus.Open, actor, Now.AddHours(2), Clock);

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", exception.Code);
    }

    [Fact]
    public async Task ATicketOfACustomerThatDoesNotExistIsNotSaved()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[0];
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 1, type, "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            Guid.CreateVersion7(), null, null, Guid.CreateVersion7(), Now, Clock);

        var exception = await Assert.ThrowsAsync<SupportDomainException>(() => SaveAsync(factory, ticket));

        Assert.Equal(SupportErrorCodes.CustomerNotFound, exception.Code);
        await using var read = factory.Services.CreateAsyncScope();
        Assert.Null(await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct));
    }

    [Fact]
    public async Task ATicketOfAnExistingCustomerIsSaved()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var customer = await SeedCustomerAsync(factory, tenant, "Novasteel SAS");
        var type = (await TypesAsync(factory, tenant))[0];
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 1, type, "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            customer, null, null, Guid.CreateVersion7(), Now, Clock);

        await SaveAsync(factory, ticket);

        await using var read = factory.Services.CreateAsyncScope();
        var found = await read.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticket.Id, Ct);
        Assert.Equal(customer, found!.CustomerId);
        Assert.Null(found.Requester);
    }

    [Fact]
    public async Task ATicketOfAnotherTenantIsNotFound()
    {
        var (factory, tenant) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        Assert.Null(await tickets.FindAsync(other, ticket.Id, Ct));
        Assert.Empty(await tickets.ListEventsAsync(other, ticket.Id, Ct));
    }

    [Fact]
    public async Task ANewTenantHasTheDefaultDueSoonOf3()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        var settings = await SettingsAsync(factory, tenant);

        Assert.Equal(tenant, settings.TenantId);
        Assert.Equal(3, settings.DueSoonBusinessDays);
        Assert.Equal(1L, settings.Version);
    }

    [Fact]
    public async Task NumberingDoesNotBumpTheSettingsVersion()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await NextCommittedAsync(factory, tenant);
        await NextCommittedAsync(factory, tenant);

        Assert.Equal(1L, (await SettingsAsync(factory, tenant)).Version);
        Assert.Equal(3L, await NextCommittedAsync(factory, tenant));
    }

    [Fact]
    public async Task ChangingTheDueSoonThresholdIsSavedWithItsVersion()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var settings = await scope.ServiceProvider.GetRequiredService<ISupportSettingsRepository>().GetAsync(tenant, Ct);
            settings.ChangeDueSoon(0, Now);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        var stored = await SettingsAsync(factory, tenant);
        Assert.Equal(0, stored.DueSoonBusinessDays);
        Assert.Equal(2L, stored.Version);
        Assert.Equal(Now, stored.UpdatedAt);
        Assert.Equal(1L, await NextCommittedAsync(factory, tenant));
    }

    [Fact]
    public async Task ATakenTypeNameIs409WhateverTheCase()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().Add(SupportTicketType.Create(
            SupportTicketTypeId.New(), tenant, "queja", 15, termIsLegal: true, requiresFormalResponse: true, isPublished: true, 9, Now));
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(SupportErrorCodes.TicketTypeNameTaken, exception.Code);
    }

    // Fase 4a (plan G5, Review Focus 1): la FK RESTRICT cierra la carrera entre borrar un tipo y radicar con él, sin la pregunta previa.
    [Fact]
    public async Task TheForeignKeyTurnsADeleteOfAUsedTypeInto422InUse()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[3];
        await SaveAsync(factory, NewTicket(tenant, type, 1, Now, Guid.CreateVersion7()));

        await using var scope = factory.Services.CreateAsyncScope();
        var types = scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>();
        types.Remove((await types.FindAsync(tenant, type.Id, Ct))!);
        var exception = await Assert.ThrowsAsync<SupportDomainException>(
            () => scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(SupportErrorCodes.TicketTypeInUse, exception.Code);
        Assert.Equal(8, (await TypesAsync(factory, tenant)).Count);
    }

    [Fact]
    public async Task TheForeignKeyTurnsATicketOnADeletedTypeInto422Unavailable()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var type = (await TypesAsync(factory, tenant))[6];
        var ticket = NewTicket(tenant, type, 1, Now, Guid.CreateVersion7());
        await using (var connection = new NpgsqlConnection(ConnectionString(factory)))
        {
            await connection.OpenAsync(Ct);
            await using var delete = new NpgsqlCommand("DELETE FROM support.ticket_types WHERE id = @id", connection);
            delete.Parameters.AddWithValue("id", type.Id.Value);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(Ct));
        }

        var exception = await Assert.ThrowsAsync<SupportDomainException>(() => SaveAsync(factory, ticket));

        Assert.Equal(SupportErrorCodes.TicketTypeUnavailable, exception.Code);
    }

    [Fact]
    public async Task TheSameTypeNameInAnotherTenantIsFine()
    {
        var (factory, tenant) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        await EnsureAsync(factory, other);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().Add(SupportTicketType.Create(
                SupportTicketTypeId.New(), tenant, "Garantía", 20, termIsLegal: false, requiresFormalResponse: false, isPublished: true, 9, Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().Add(SupportTicketType.Create(
                SupportTicketTypeId.New(), other, "garantía", 20, termIsLegal: false, requiresFormalResponse: false, isPublished: true, 9, Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal("Garantía", (await TypesAsync(factory, tenant))[^1].Name);
        Assert.Equal("garantía", (await TypesAsync(factory, other))[^1].Name);
    }

    // Pre-flight R1: el límite de «por vencer» es nullable (null = umbral 0 = apagado) y los vencidos y por vencer sólo cuentan
    // los estados con el reloj corriendo. Calendario de lunes a viernes; hoy 2026-11-17.
    [Fact]
    public async Task TheTotalsCountEveryStatusAndOnlyRunningTicketsAreDue()
    {
        var (factory, tenant) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        await EnsureAsync(factory, other);
        var actor = Guid.CreateVersion7();
        var documents = (await TypesAsync(factory, tenant))[1];                            // 10 días
        var overdue = NewTicket(tenant, documents, 1, At(11, 2), actor);                  // vence 11-16
        overdue.ChangeStatus(SupportTicketStatus.Open, actor, At(11, 3), Clock);
        var dueSoon = NewTicket(tenant, documents, 2, At(11, 10), actor);                 // vence 11-24
        var finished = NewTicket(tenant, documents, 3, At(11, 2), actor);                 // vence 11-16, resuelto
        finished.ChangeStatus(SupportTicketStatus.Resolved, actor, At(11, 4), Clock);
        var foreign = NewTicket(other, (await TypesAsync(factory, other))[1], 1, At(11, 2), actor);
        foreach (var ticket in new[] { overdue, dueSoon, finished, foreign })
        {
            await SaveAsync(factory, ticket);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var today = new DateOnly(2026, 11, 17);
        var totals = await tickets.TotalsAsync(tenant, SupportTicketKind.External, null, today, new DateOnly(2026, 11, 24), Ct);
        var off = await tickets.TotalsAsync(tenant, SupportTicketKind.External, null, today, null, Ct);

        Assert.Equal(SupportTicketStatuses.Ordered, totals.ByStatus.Keys.Order());
        Assert.Equal(
            [(SupportTicketStatus.New, 1), (SupportTicketStatus.Open, 1), (SupportTicketStatus.WaitingOnCustomer, 0),
             (SupportTicketStatus.WaitingInternal, 0), (SupportTicketStatus.Resolved, 1), (SupportTicketStatus.Closed, 0)],
            SupportTicketStatuses.Ordered.Select(status => (status, totals.ByStatus[status])));
        Assert.Equal(1, totals.Overdue);
        Assert.Equal(1, totals.DueSoon);
        Assert.Equal(1, off.Overdue);
        Assert.Equal(0, off.DueSoon);
    }

    [Fact]
    public async Task TheListFiltersPagesAndStaysInsideTheTenant()
    {
        var (factory, tenant) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        await EnsureAsync(factory, other);
        var actor = Guid.CreateVersion7();
        var assignee = Guid.CreateVersion7();
        var types = await TypesAsync(factory, tenant);
        var overdue = NewTicket(tenant, types[1], 1, At(11, 2), actor);                    // vence 11-16
        overdue.ChangeStatus(SupportTicketStatus.Open, actor, At(11, 3), Clock);
        overdue.Assign(assignee, actor, At(11, 3));
        var newest = NewTicket(tenant, types[7], 2, At(11, 12), actor);                    // sin término
        var resolved = NewTicket(tenant, types[1], 3, At(11, 4), actor);                   // vence 11-18
        resolved.ChangeStatus(SupportTicketStatus.Resolved, actor, At(11, 5), Clock);
        var foreign = NewTicket(other, (await TypesAsync(factory, other))[1], 1, At(11, 13), actor);
        foreach (var ticket in new[] { overdue, newest, resolved, foreign })
        {
            await SaveAsync(factory, ticket);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var all = await tickets.ListAsync(Filter(tenant) with { PageSize = 2 }, Ct);
        var secondPage = await tickets.ListAsync(Filter(tenant) with { Page = 2, PageSize = 2 }, Ct);
        var pastDue = await tickets.ListAsync(
            Filter(tenant) with { RunningOnly = true, DueBefore = new DateOnly(2026, 11, 19) }, Ct);
        var dueBetween = await tickets.ListAsync(
            Filter(tenant) with { DueBetween = (new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18)) }, Ct);
        var newOnly = await tickets.ListAsync(Filter(tenant) with { Statuses = [SupportTicketStatus.New] }, Ct);
        var byType = await tickets.ListAsync(Filter(tenant) with { TypeId = types[7].Id.Value }, Ct);
        var mine = await tickets.ListAsync(Filter(tenant) with { AssigneeMemberId = assignee }, Ct);
        var unassigned = await tickets.ListAsync(Filter(tenant) with { UnassignedOnly = true }, Ct);
        var byNumber = await tickets.ListAsync(Filter(tenant) with { Number = 3 }, Ct);

        Assert.Equal(3, all.Total);
        Assert.Equal([newest.Id, resolved.Id], all.Items.Select(ticket => ticket.Id));
        Assert.Equal([overdue.Id], secondPage.Items.Select(ticket => ticket.Id));
        Assert.Equal([overdue.Id], pastDue.Items.Select(ticket => ticket.Id));
        Assert.Equal([resolved.Id], dueBetween.Items.Select(ticket => ticket.Id));
        Assert.Equal([newest.Id], newOnly.Items.Select(ticket => ticket.Id));
        Assert.Equal([newest.Id], byType.Items.Select(ticket => ticket.Id));
        Assert.Equal([overdue.Id], mine.Items.Select(ticket => ticket.Id));
        Assert.Equal([newest.Id, resolved.Id], unassigned.Items.Select(ticket => ticket.Id));
        Assert.Equal([resolved.Id], byNumber.Items.Select(ticket => ticket.Id));
    }

    [Fact]
    public async Task TheSearchMatchesTheSubjectTheRequesterNamesAndTheGivenCustomers()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var actor = Guid.CreateVersion7();
        var type = (await TypesAsync(factory, tenant))[0];
        var customer = await SeedCustomerAsync(factory, tenant, "Novasteel SAS");
        var bySubject = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 1, type, "Factura de octubre", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email, null,
            new TicketRequester("Ana", "Gómez", null, "CC", "1020304050", "ana@example.com", null, null, "CO", null),
            null, actor, At(11, 2), Clock);
        var byRequester = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 2, type, "Garantía", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            null, new TicketRequester("Bruno", "Díaz", "Aceros 50% SAS", null, null, "bruno@example.com", null, null, "CO", null),
            null, actor, At(11, 3), Clock);
        var byCustomer = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 3, type, "Pedido incompleto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email, customer, null, null, actor, At(11, 4), Clock);
        foreach (var ticket in new[] { bySubject, byRequester, byCustomer })
        {
            await SaveAsync(factory, ticket);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        async Task<IEnumerable<SupportTicketId>> SearchAsync(string? term, IReadOnlyCollection<Guid>? customers = null) =>
            (await tickets.ListAsync(Filter(tenant) with { SearchTerm = term, SearchCustomerIds = customers }, Ct)).Items.Select(item => item.Id);

        Assert.Equal([bySubject.Id], await SearchAsync("FACTURA"));
        Assert.Equal([byRequester.Id], await SearchAsync("díaz"));
        Assert.Equal([byRequester.Id], await SearchAsync("bruno"));
        // Los comodines del usuario son literales: «50%» no es «50 y cualquier cosa».
        Assert.Equal([byRequester.Id], await SearchAsync("50%"));
        Assert.Equal([byCustomer.Id], await SearchAsync("sin coincidencias", [customer]));
        // El documento no entra en la búsqueda de texto (plan, Tarea 9: asunto y nombres del solicitante).
        Assert.Empty(await SearchAsync("1020304050"));
        Assert.Equal([byCustomer.Id, byRequester.Id, bySubject.Id], await SearchAsync("   "));
        Assert.Equal([byCustomer.Id, byRequester.Id, bySubject.Id], await SearchAsync(null));
    }

    // La búsqueda de la bandeja por cliente tiene tope (revisión final, B2): sus ids viajan como un solo arreglo al filtro de
    // tickets. Sembrar uno más que el tope con un solo INSERT es barato, así que se prueba el tope real y no uno inyectado.
    [Fact]
    public async Task TheCustomerSearchStopsAtTheLimitInNameOrder()
    {
        var (factory, tenant) = await NewTenantAsync();
        var (_, other) = await NewTenantAsync();
        await SeedNamedCustomersAsync(factory, tenant, ISupportCustomerLookup.SearchLimit + 1);
        await SeedNamedCustomersAsync(factory, other, 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<ISupportCustomerLookup>().SearchIdsAsync(tenant, "cliente", Ct);

        Assert.Equal(200, ISupportCustomerLookup.SearchLimit);
        // El que queda fuera es el último por nombre: «Cliente 201».
        Assert.Equal((await CustomerIdsAsync(factory, tenant, "Cliente 201")).Order(), found.Order());
    }

    // Revisión final m3: los CHECK de los dos enums que faltaban. La base rechaza un valor fuera del enum aunque lo escriba SQL crudo.
    [Theory]
    [InlineData("UPDATE support.tickets SET manual_medium = 'Fax' WHERE id = @id", "CK_tickets_manual_medium")]
    [InlineData("UPDATE support.ticket_events SET kind = 'Deleted' WHERE ticket_id = @id", "CK_ticket_events_kind")]
    public async Task AValueOutsideTheEnumIsRejectedByItsCheck(string sql, string constraint)
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand(sql, connection);
        update.Parameters.AddWithValue("id", ticket.Id.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    // El alta manual sin medio no existe hoy, pero las fases 2 y 3 lo dejan en NULL: el CHECK lo admite.
    [Fact]
    public async Task TheManualMediumCheckAcceptsNull()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE support.tickets SET manual_medium = NULL WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", ticket.Id.Value);

        Assert.Equal(1, await update.ExecuteNonQueryAsync(Ct));
    }

    // Spec 2026-11-22: tareas internas.
    [Fact]
    public async Task ATaskRoundTrips()
    {
        var (factory, tenant) = await NewTenantAsync();
        var creator = Guid.CreateVersion7();
        var assignee = Guid.CreateVersion7();

        var saved = await SupportTicketSeed.SaveInternalTicketAsync(factory, tenant, creator, assignee, new DateOnly(2030, 1, 15));

        var found = await SupportTicketSeed.FindTicketAsync(factory, tenant, saved.Id);
        Assert.True(found.IsInternal);
        Assert.Equal(creator, found.CreatedByMemberId);
        Assert.Equal(assignee, found.AssigneeMemberId);
        Assert.Equal(SupportReplyChannel.None, found.ReplyChannel);
        Assert.Equal(SupportTicketIntake.Internal, found.Intake);
        Assert.Equal(new DateOnly(2030, 1, 15), found.DueOn);
        Assert.Null(found.ManualMedium);
        Assert.Null(found.CustomerId);
        Assert.Null(found.Requester);
        Assert.Equal("Tarea interna", found.TypeName);
    }

    [Fact]
    public async Task TheInternalCheckRejectsATaskWithACustomer()
    {
        var (factory, tenant) = await NewTenantAsync();
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, tenant, Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE support.tickets SET customer_id = gen_random_uuid() WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", task.Id.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_tickets_internal", exception.ConstraintName);
    }

    [Fact]
    public async Task TheInternalCheckRejectsAnExternalTicketWithoutAReplyChannel()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE support.tickets SET reply_channel = 'None' WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", ticket.Id.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));

        Assert.Equal("CK_tickets_internal", exception.ConstraintName);
    }

    [Fact]
    public async Task ATenantHasOneInternalType()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO support.ticket_types (id, tenant_id, name, term_is_legal, requires_formal_response, is_published, is_active,
                is_internal, position, created_at, updated_at, version)
            VALUES (gen_random_uuid(), @tenant, 'Otra tarea', false, false, false, true, true, 10, now(), now(), 1)
            """,
            connection);
        insert.Parameters.AddWithValue("tenant", tenant);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("UX_ticket_types_tenant_internal", exception.ConstraintName);
    }

    [Fact]
    public async Task ProvisioningSeedsTheInternalTypeLast()
    {
        var (factory, tenant) = await NewTenantAsync();

        await EnsureAsync(factory, tenant);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var select = new NpgsqlCommand(
            "SELECT name, position, is_internal FROM support.ticket_types WHERE tenant_id = @tenant ORDER BY position", connection);
        select.Parameters.AddWithValue("tenant", tenant);
        var rows = new List<(string Name, int Position, bool IsInternal)>();
        await using (var reader = await select.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2)));
            }
        }

        Assert.Equal(9, rows.Count);
        Assert.Equal(("Tarea interna", 9, true), rows[^1]);
        Assert.All(rows.Take(8), row => Assert.False(row.IsInternal));
        Assert.Equal(8, (await TypesAsync(factory, tenant)).Count);
        await using var scope = factory.Services.CreateAsyncScope();
        var internalType = await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().FindInternalAsync(tenant, Ct);
        Assert.NotNull(internalType);
        Assert.True(internalType.IsInternal);
        Assert.Equal(9, internalType.Position);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().FindAsync(tenant, internalType.Id, Ct));
    }

    [Fact]
    public async Task EnsuringTheInternalTypeTwiceKeepsOne()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var provisioner = scope.ServiceProvider.GetRequiredService<ISupportProvisioner>();
            await provisioner.EnsureInternalTypeAsync(tenant, Ct);
            await provisioner.EnsureInternalTypeAsync(tenant, Ct);
        }

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM support.ticket_types WHERE tenant_id = @tenant AND is_internal", connection);
        count.Parameters.AddWithValue("tenant", tenant);
        Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
    }

    // Fase 4b (H9): el documento de la respuesta formal se guarda en el mismo SaveChanges que la respuesta y se lee igual: la decisión
    // con sus saltos de línea, los anexos en orden (jsonb), la firma y el hash.
    [Fact]
    public async Task AReplyDocumentRoundTrips()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);
        var (reply, document) = NewFormalReply(tenant, ticket, ["Factura 123", "Extracto de octubre"]);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var replies = scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
            replies.Add(reply);
            replies.AddDocument(document);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
        var found = await repository.FindDocumentAsync(tenant, ticket.Id, reply.Id, Ct);
        var listed = await repository.ListDocumentsAsync(tenant, ticket.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(document.Decision, found.Decision);
        Assert.Equal(document.NextStep, found.NextStep);
        Assert.Equal(document.SignerMemberId, found.SignerMemberId);
        Assert.Equal("Ana Gómez", found.SignerName);
        Assert.Equal("Coordinadora de servicio", found.SignerTitle);
        Assert.Equal(["Factura 123", "Extracto de octubre"], found.Annexes);
        Assert.Equal(document.StorageKey, found.StorageKey);
        Assert.Equal(document.FileName, found.FileName);
        Assert.Equal(document.SizeBytes, found.SizeBytes);
        Assert.Equal(document.Sha256Hex, found.Sha256Hex);
        Assert.Equal(document.RespondedOn, found.RespondedOn);
        Assert.Equal(document.DocumentClientId, found.DocumentClientId);
        Assert.Equal(ticket.Id, found.TicketId);
        Assert.Equal([reply.Id], listed.Select(item => item.ReplyId));
        Assert.Null(await repository.FindDocumentAsync(Guid.CreateVersion7(), ticket.Id, reply.Id, Ct));
        Assert.Empty(await repository.ListDocumentsAsync(Guid.CreateVersion7(), ticket.Id, Ct));
    }

    // H9: el único borrado de un documento es la cascada de una respuesta que el canal rechazó antes de enviar (SendTicketReply la
    // quita): para el solicitante esa respuesta nunca existió.
    [Fact]
    public async Task RemovingANeverSentReplyRemovesItsDocument()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, Guid.CreateVersion7());
        await SaveAsync(factory, ticket);
        var (reply, document) = NewFormalReply(tenant, ticket, []);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var replies = scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
            replies.Add(reply);
            replies.AddDocument(document);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var replies = scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
            replies.Remove((await replies.FindByClientIdAsync(tenant, ticket.Id, reply.ClientId, Ct))!);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM support.ticket_reply_documents WHERE reply_id = @id", connection);
        count.Parameters.AddWithValue("id", reply.Id);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    private static (SupportTicketReply Reply, SupportReplyDocument Document) NewFormalReply(
        Guid tenant, SupportTicket ticket, IReadOnlyList<string> annexes)
    {
        var reply = SupportTicketReply.Start(
            tenant, ticket.Id, Guid.CreateVersion7(), SupportReplyChannel.Email, SupportReplyDelivery.Email,
            "Te enviamos la respuesta formal.", null, Guid.CreateVersion7(), Now);
        var document = SupportReplyDocument.Create(
            reply,
            "Revisamos tu caso.\n\nDecidimos devolver el dinero.",
            "Puedes acudir a la SIC.",
            Guid.CreateVersion7(),
            "Ana Gómez",
            "Coordinadora de servicio",
            annexes,
            $"support/tenants/{tenant:N}/replies/2026/11/{Guid.CreateVersion7():N}.pdf",
            SupportReplyDocument.FileNameFor(ticket.DisplayNumber),
            [0x25, 0x50, 0x44, 0x46],
            new DateOnly(2026, 11, 13),
            Now);
        return (reply, document);
    }

    // Fase 4b (H15): resolver un ticket cargado guarda su encuesta en el mismo SaveChanges, con el número de resolución, y se lee por
    // tenant y ticket.
    [Fact]
    public async Task ASurveyRoundTrips()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, actor);
        await SaveAsync(factory, ticket);

        await ResolveAsync(factory, tenant, ticket.Id, actor);

        await using var read = factory.Services.CreateAsyncScope();
        var tickets = read.ServiceProvider.GetRequiredService<ISupportTicketRepository>();
        var survey = Assert.Single(await tickets.ListSurveysAsync(tenant, ticket.Id, Ct));
        Assert.Equal((tenant, ticket.Id, 1, SupportSurveyStatus.Pending), (survey.TenantId, survey.TicketId, survey.ResolutionNumber, survey.Status));
        Assert.Equal((Now.AddHours(1), 0), (survey.NextAttemptAt, survey.Attempts));
        Assert.Null(survey.TokenHash);
        Assert.Empty(await tickets.ListSurveysAsync(Guid.CreateVersion7(), ticket.Id, Ct));
    }

    // H15: la idempotencia por (ticket, número de resolución) la cierra el índice único, no la consulta.
    [Fact]
    public async Task TheResolutionUniqueIndexRejectsADuplicate()
    {
        var (factory, tenant) = await NewTenantAsync();
        await EnsureAsync(factory, tenant);
        var actor = Guid.CreateVersion7();
        var ticket = NewTicket(tenant, (await TypesAsync(factory, tenant))[0], 1, Now, actor);
        await SaveAsync(factory, ticket);
        await ResolveAsync(factory, tenant, ticket.Id, actor);

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO support.ticket_surveys (id, tenant_id, ticket_id, resolution_number, status, attempts, next_attempt_at, created_at, updated_at)
            SELECT gen_random_uuid(), tenant_id, ticket_id, resolution_number, status, attempts, next_attempt_at, created_at, updated_at
            FROM support.ticket_surveys WHERE ticket_id = @id
            """,
            connection);
        insert.Parameters.AddWithValue("id", ticket.Id.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("UX_ticket_surveys_ticket_resolution", exception.ConstraintName);
    }

    private static async Task ResolveAsync(QepApiFactory factory, Guid tenant, SupportTicketId ticketId, Guid actor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenant, ticketId, Ct);
        loaded!.ChangeStatus(SupportTicketStatus.Resolved, actor, Now.AddHours(1), Clock);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    // Revisión final m2: SupportUserReferenceProbe busca por membresía sola, sin tenant, en las dos columnas.
    [Theory]
    [InlineData("IX_tickets_assignee", "tickets", "(assignee_member_id)")]
    [InlineData("IX_ticket_events_actor", "ticket_events", "(actor_member_id)")]
    public async Task TheUserReferenceProbeColumnsAreIndexed(string index, string table, string columns)
    {
        var (factory, _) = await NewTenantAsync();

        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var select = new NpgsqlCommand(
            "SELECT tablename, indexdef FROM pg_indexes WHERE schemaname = 'support' AND indexname = @index", connection);
        select.Parameters.AddWithValue("index", index);
        await using var reader = await select.ExecuteReaderAsync(Ct);

        Assert.True(await reader.ReadAsync(Ct), $"The index {index} does not exist.");
        Assert.Equal(table, reader.GetString(0));
        Assert.EndsWith($"USING btree {columns}", reader.GetString(1));
    }

    private static async Task SeedNamedCustomersAsync(QepApiFactory factory, Guid tenant, int count)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            SELECT gen_random_uuid(), @tenant, 'Cliente ' || lpad(n::text, 3, '0'), true, 'Incomplete',
                   'CO.' || replace(gen_random_uuid()::text, '-', ''), false, false, 1, now(), now()
            FROM generate_series(1, @count) AS n
            """,
            connection);
        insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("count", count);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<Guid>> CustomerIdsAsync(QepApiFactory factory, Guid tenant, string exceptName)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(factory));
        await connection.OpenAsync(Ct);
        await using var select = new NpgsqlCommand(
            "SELECT id FROM customers.customers WHERE tenant_id = @tenant AND name <> @except", connection);
        select.Parameters.AddWithValue("tenant", tenant);
        select.Parameters.AddWithValue("except", exceptName);
        var ids = new List<Guid>();
        await using var reader = await select.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static string ConnectionString(QepApiFactory factory) =>
        factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;

    private static SupportTicketFilter Filter(Guid tenant) =>
        new(tenant, Statuses: null, TypeId: null, AssigneeMemberId: null, UnassignedOnly: false, RunningOnly: false, DueBefore: null,
            DueBetween: null, Number: null, SearchTerm: null, SearchCustomerIds: null, Page: 1, PageSize: 25);

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 15, 0, 0, TimeSpan.Zero);

    private static SupportTicket NewTicket(
        Guid tenant,
        SupportTicketType type,
        long number,
        DateTimeOffset receivedAt,
        Guid actor,
        SupportTicketPriority priority = SupportTicketPriority.Medium) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, number, type, $"Asunto {number}", "Descripción", priority,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email, null,
            new TicketRequester("Ana", "Gómez", null, null, null, "ana@example.com", null, null, "CO", null),
            null, actor, receivedAt, Clock);

    private async Task<(QepApiFactory Factory, Guid Tenant)> NewTenantAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenantId = NewTenantId();
        await SeedTenantAsync(factory, tenantId);
        return (factory, Guid.Parse(tenantId));
    }

    private static async Task EnsureAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
    }

    private static async Task<IReadOnlyList<SupportTicketType>> TypesAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct);
    }

    private static async Task<SupportTenantSettings> SettingsAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportSettingsRepository>().GetAsync(tenant, Ct);
    }

    private static async Task SaveAsync(QepApiFactory factory, SupportTicket ticket)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<long> NextCommittedAsync(QepApiFactory factory, Guid tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().BeginTransactionAsync(Ct);
        var number = await scope.ServiceProvider.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenant, Ct);
        await transaction.CommitAsync(Ct);
        return number;
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
