using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §6, D22, D24, D33, D34 y P9 del plan: el tablero que la pantalla dibuja sin lógica propia.</summary>
public sealed class BoardApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId, Guid OwnerMemberId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), customerId, owner);
    }

    private static async Task<DealDetailDto> CreateAsync(Setup setup, decimal? amount, string? currency = null, Guid? owner = null, Guid? customerId = null)
    {
        var response = await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(customerId ?? setup.CustomerId, "Dotación", amount, currency, null, owner), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static async Task<PipelineBoardDto> BoardAsync(Setup setup, string? owner = null) =>
        (await setup.Client.GetFromJsonAsync<PipelineBoardDto>(
            $"{PipelineUrl(setup.Tenant.TenantId)}/board{(owner is null ? string.Empty : $"?owner={owner}")}", Ct))!;

    private static BoardColumnDto Column(PipelineBoardDto board, string name) => board.Columns.Single(column => column.Name == name);

    // BFF: todas las columnas, incluso en cero; sin owner, "me".
    [Fact]
    public async Task EveryColumnComesEvenAtZero()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;

        var board = await BoardAsync(setup);

        Assert.Equal("me", board.Owner);
        Assert.Equal(["Nuevo", "Calificado", "Cotizado", "Negociación", "Ganado", "Perdido"], board.Columns.Select(column => column.Name));
        Assert.All(board.Columns, column =>
        {
            Assert.Equal(0, column.Count);
            Assert.Empty(column.Totals);
            Assert.Empty(column.Deals);
        });
        Assert.Equal((50, true), (Column(board, "Cotizado").Probability, Column(board, "Cotizado").IsQuotationStage));
        Assert.Equal((0, 0), (board.Summary.OpenCount, board.Summary.StalledCount));
        Assert.Empty(board.Summary.OpenTotals);
    }

    // BFF: la franja de indicadores no suma columnas en el navegador. Sólo etapas abiertas, por moneda (D24); el ponderado es la
    // suma de los ponderados ya redondeados de cada columna (0,11 + 0,02 = 0,13, y no el 0,12 de redondear una sola vez), para
    // que la franja cuadre con lo que la pantalla muestra. Ganado y Perdido no entran.
    [Fact]
    public async Task TheSummaryAddsTheOpenColumnsByCurrencyAndLeavesWonAndLostOut()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        await CreateAsync(setup, 1.05m);
        await CreateAsync(setup, 500m, "USD");
        await CreateAsync(setup, null);
        var negotiating = await CreateAsync(setup, 0.02m);
        var won = await CreateAsync(setup, 1_000m);
        var lost = await CreateAsync(setup, 2_000m);
        var stages = await BoardAsync(setup);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET stage_id = @s WHERE id = @id",
            ("s", Column(stages, "Negociación").StageId), ("id", negotiating.Id));
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals/{won.Id}/win", new DealVersionRequest(won.Version), Ct)).EnsureSuccessStatusCode();
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET stage_id = @s, closed_at = @at WHERE id = @id",
            ("s", Column(stages, "Perdido").StageId), ("at", DateTimeOffset.UtcNow), ("id", lost.Id));

        var board = await BoardAsync(setup);

        Assert.Equal((1, 1), (Column(board, "Ganado").Count, Column(board, "Perdido").Count));
        Assert.Equal(4, board.Summary.OpenCount);
        Assert.Equal(0, board.Summary.StalledCount);
        Assert.Equal(
            [new CurrencyTotalDto("COP", 1.07m, 0.13m), new CurrencyTotalDto("USD", 500m, 50m)],
            board.Summary.OpenTotals);
        var openColumns = board.Columns.Where(column => column.Kind == "Open").ToArray();
        Assert.Equal(openColumns.Sum(column => column.Count), board.Summary.OpenCount);
        Assert.Equal(
            openColumns.SelectMany(column => column.Totals).Where(total => total.Currency == "COP").Sum(total => total.Weighted),
            board.Summary.OpenTotals.Single(total => total.Currency == "COP").Weighted);
    }

    // D23: la franja sigue el mismo filtro de dueño que las columnas, también para el estancado.
    [Fact]
    public async Task TheSummaryFollowsTheOwnerFilter()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (member, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Laura Gómez");
        await CreateAsync(setup, 100m);
        var theirs = await CreateAsync(setup, 200m, owner: member);
        var fourDaysAgo = DateTimeOffset.UtcNow.AddDays(-4).AddHours(-1);
        await ExecuteAsync(connectionString,
            "UPDATE pipeline.deals SET stage_entered_at = @at, last_changed_at = @at WHERE id = @id",
            ("at", fourDaysAgo), ("id", theirs.Id));

        var mine = (await BoardAsync(setup, "me")).Summary;
        var all = (await BoardAsync(setup, "all")).Summary;

        Assert.Equal((1, 0), (mine.OpenCount, mine.StalledCount));
        Assert.Equal([new CurrencyTotalDto("COP", 100m, 10m)], mine.OpenTotals);
        Assert.Equal((2, 1), (all.OpenCount, all.StalledCount));
        Assert.Equal([new CurrencyTotalDto("COP", 300m, 30m)], all.OpenTotals);
    }

    // D22 y D33: el estancado de la franja es de TODOS los negocios abiertos, no de las 200 tarjetas por columna. Los cinco que
    // entraron a la etapa más tarde quedan fuera del tope; tres están quietos y dos son de un cliente que acaba de escribir, así que
    // la actividad se busca también para clientes que no tienen tarjeta.
    [Fact]
    public async Task TheStalledCountCoversDealsBeyondTheCardCap()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (writer, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Marta");
        var stages = (await setup.Client.GetFromJsonAsync<PipelineStagesDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/stages", Ct))!;
        // Los instantes salen del reloj de la prueba, no de now() de Postgres (ver AStalledDealIsFlaggedUntilTheCustomerWrites).
        var now = DateTimeOffset.UtcNow;
        var fourDaysAgo = now.AddDays(-4).AddHours(-1);
        // n = 1..5 entraron a la etapa más tarde (posiciones 201..205) y llevan cuatro días quietos: 1..3 de Laura, 4..5 de Marta.
        await ExecuteAsync(connectionString,
            """
            INSERT INTO pipeline.deals (id, tenant_id, pipeline_id, customer_id, owner_member_id, title, amount, currency, stage_id,
                stage_entered_at, source, last_changed_at, created_at, updated_at, version, history_count)
            SELECT gen_random_uuid(), @t, @p, CASE WHEN n IN (4, 5) THEN @w ELSE @c END, @o, 'Carga ' || n, 10, 'COP', @s,
                @now - make_interval(mins => n), 'Manual', CASE WHEN n <= 5 THEN @old ELSE @now END, @now, @now, 1, 0
            FROM generate_series(1, 205) AS n
            """,
            ("t", setup.Tenant.TenantId), ("p", stages.PipelineId), ("c", setup.CustomerId), ("w", writer), ("o", setup.OwnerMemberId),
            ("s", stages.Stages[0].Id), ("now", now), ("old", fourDaysAgo));
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, writer, now, now);

        var board = await BoardAsync(setup);
        var column = Column(board, "Nuevo");

        Assert.Equal((205, 200), (column.Count, column.Deals.Count));
        Assert.DoesNotContain(column.Deals, card => card.IsStalled);
        Assert.Equal((205, 3), (board.Summary.OpenCount, board.Summary.StalledCount));
        Assert.Equal([new CurrencyTotalDto("COP", 2_050m, 205m)], board.Summary.OpenTotals);
    }

    // Review Focus 5 (D24, D34): COP y USD salen como dos totales, en el orden del catálogo; el ponderado usa la probabilidad
    // de la etapa; el negocio sin monto cuenta pero no suma.
    [Fact]
    public async Task TotalsAreGroupedByCurrencyAndANoAmountDealOnlyCounts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await CreateAsync(setup, 500m, "USD");
        await CreateAsync(setup, 1_000_000m);
        await CreateAsync(setup, 200_000m);
        await CreateAsync(setup, null);

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal(4, column.Count);
        Assert.Equal(4, column.Deals.Count);
        Assert.Equal(
            [new CurrencyTotalDto("COP", 1_200_000m, 120_000m), new CurrencyTotalDto("USD", 500m, 50m)],
            column.Totals);
    }

    // El ponderado redondea como el resto del dinero del repo (lejos de cero): 1,05 × 10 % = 0,105 → 0,11, no el 0,10 del
    // redondeo bancario.
    [Fact]
    public async Task TheWeightedTotalRoundsAMidpointAwayFromZero()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await CreateAsync(setup, 1.05m);

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal(10, column.Probability);
        Assert.Equal([new CurrencyTotalDto("COP", 1.05m, 0.11m)], column.Totals);
    }

    // D34: una columna con sólo negocios sin monto (lo normal después del triage) los cuenta y no trae totales, ni siquiera uno
    // en cero por moneda.
    [Fact]
    public async Task AColumnWhoseDealsHaveNoAmountCountsThemAndHasNoTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetupAsync(factory, database.GetConnectionString());
        using var _ = setup.Client;
        await CreateAsync(setup, null);
        await CreateAsync(setup, null, "USD");

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal((2, 2), (column.Count, column.Deals.Count));
        Assert.Empty(column.Totals);
    }

    // D23: el filtro por dueño es foco; "all" ve todo el tenant.
    [Fact]
    public async Task OwnerMeShowsMineAndAllShowsEveryone()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (member, _) = await SeedMemberAsync(connectionString, setup.Tenant.TenantId, "Laura Gómez");
        await CreateAsync(setup, 100m);
        await CreateAsync(setup, 200m, owner: member);

        var mine = Column(await BoardAsync(setup, "me"), "Nuevo");
        var all = Column(await BoardAsync(setup, "all"), "Nuevo");
        var invalid = await setup.Client.GetAsync($"{PipelineUrl(setup.Tenant.TenantId)}/board?owner=team", Ct);

        Assert.Equal((1, setup.OwnerMemberId), (mine.Count, Assert.Single(mine.Deals).OwnerMemberId));
        Assert.Equal(2, all.Count);
        Assert.Contains(all.Deals, card => card.OwnerName == "Laura Gómez");
        Assert.Equal(["owner"], (await ProblemAsync(invalid)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
    }

    // D33: Won y Lost sólo con lo cerrado en los últimos 30 días.
    [Fact]
    public async Task ClosedColumnsOnlyShowTheLastThirtyDays()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var recent = await CreateAsync(setup, 100m);
        var old = await CreateAsync(setup, 200m);
        foreach (var deal in new[] { recent, old })
        {
            (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals/{deal.Id}/win", new DealVersionRequest(deal.Version), Ct)).EnsureSuccessStatusCode();
        }

        // El instante lo calcula la prueba con el reloj de la app, no Postgres: 31 días atrás queda fuera de la ventana de 30
        // aunque el reloj del contenedor esté desfasado unas horas.
        var thirtyOneDaysAgo = DateTimeOffset.UtcNow.AddDays(-31);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET closed_at = @at WHERE id = @id", ("at", thirtyOneDaysAgo), ("id", old.Id));

        var won = Column(await BoardAsync(setup), "Ganado");

        Assert.Equal(1, won.Count);
        Assert.Equal(recent.Id, Assert.Single(won.Deals).Id);
        Assert.Equal([new CurrencyTotalDto("COP", 100m, 100m)], won.Totals);
    }

    // D33 y P9: máximo 200 tarjetas, con el Count y las sumas de toda la columna; la que más lleva en la etapa, primero.
    [Fact]
    public async Task AColumnBringsAtMostTwoHundredCardsWithTheRealCount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var stages = (await setup.Client.GetFromJsonAsync<PipelineStagesDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/stages", Ct))!;
        // Los instantes salen del reloj de la prueba, no de now() de Postgres (ver AStalledDealIsFlaggedUntilTheCustomerWrites).
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connectionString,
            """
            INSERT INTO pipeline.deals (id, tenant_id, pipeline_id, customer_id, owner_member_id, title, amount, currency, stage_id,
                stage_entered_at, source, last_changed_at, created_at, updated_at, version, history_count)
            SELECT gen_random_uuid(), @t, @p, @c, @o, 'Carga ' || n, 10, 'COP', @s, @now - make_interval(mins => n), 'Manual', @now, @now, @now, 1, 0
            FROM generate_series(1, 205) AS n
            """,
            ("t", setup.Tenant.TenantId), ("p", stages.PipelineId), ("c", setup.CustomerId), ("o", setup.OwnerMemberId), ("s", stages.Stages[0].Id),
            ("now", now));

        var column = Column(await BoardAsync(setup), "Nuevo");

        Assert.Equal((205, 200), (column.Count, column.Deals.Count));
        Assert.Equal([new CurrencyTotalDto("COP", 2_050m, 205m)], column.Totals);
        Assert.Equal("Carga 205", column.Deals[0].Title);
    }

    // D22 y §5.2: estancado si pasó más que los días de la etapa desde la última actividad; un mensaje del cliente cuenta.
    [Fact]
    public async Task AStalledDealIsFlaggedUntilTheCustomerWrites()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var deal = await CreateAsync(setup, 100m);
        // El instante lo calcula la prueba con el reloj de la app, no Postgres con now(): si el reloj del contenedor (Docker/WSL2)
        // va adelantado, now() - 4 días queda a menos de 4 días del reloj de la API y DaysInStage da 3. La hora de más es margen.
        var fourDaysAgo = DateTimeOffset.UtcNow.AddDays(-4).AddHours(-1);
        await ExecuteAsync(connectionString,
            "UPDATE pipeline.deals SET stage_entered_at = @at, last_changed_at = @at WHERE id = @id",
            ("at", fourDaysAgo), ("id", deal.Id));

        var stalled = Assert.Single(Column(await BoardAsync(setup), "Nuevo").Deals);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var active = Assert.Single(Column(await BoardAsync(setup), "Nuevo").Deals);

        Assert.Equal((true, 4), (stalled.IsStalled, stalled.DaysInStage));
        Assert.False(active.IsStalled);
    }

    // BFF: la tarjeta trae resueltos el cliente (con su completitud), el dueño y el número de la principal.
    [Fact]
    public async Task TheCardCarriesTheNamesAndThePrimaryNumber()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplySentAsync(
                new QuotationFact(setup.Tenant.TenantId, Guid.CreateVersion7(), "COT-0042", setup.CustomerId, setup.OwnerMemberId, 750m, "COP", null),
                DateTimeOffset.UtcNow, Ct);
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        var card = Assert.Single(Column(await BoardAsync(setup), "Cotizado").Deals);

        Assert.Equal(("COT-0042", (decimal?)750m, false), (card.PrimaryQuotationNumber, card.Amount, card.CustomerIsComplete));
        Assert.False(string.IsNullOrWhiteSpace(card.CustomerName));
        Assert.False(string.IsNullOrWhiteSpace(card.OwnerName));
    }
}
