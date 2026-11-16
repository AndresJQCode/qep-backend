using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fase 4b, Tarea 11 (H16, H17): la cola de las encuestas contra la base real. Host propio por prueba, sin los workers de Soporte: la
/// prueba reclama y procesa con <see cref="SupportSurveySender"/> a mano. El correo y el aviso del hub son dobles que anotan; la decisión
/// de canal la amarran <c>SupportSurveySenderTests</c>.
/// </summary>
public sealed class SupportSurveyApiTests : IAsyncLifetime
{
    private readonly RecordingSupportSurveyEmail _email = new();
    private readonly RecordingSupportRealtime _realtime = new();
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        (_database, _factory) = await OpenOwnHostAsync(services =>
        {
            services.RemoveAll<ISupportSurveyEmail>();
            services.AddSingleton<ISupportSurveyEmail>(_email);
            services.RemoveAll<ISupportRealtime>();
            services.AddSingleton<ISupportRealtime>(_realtime);
        });

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    // FOR UPDATE SKIP LOCKED con locked_until: dos workers a la vez reparten las encuestas sin repetir ninguna, y una ya reclamada no se
    // vuelve a reclamar mientras dure su reclamo.
    [Fact]
    public async Task TwoWorkersNeverClaimTheSameRow()
    {
        var scenario = await StartAsync(Factory);
        var surveys = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        }

        surveys.AddRange(await SurveyIdsAsync(scenario));
        var claims = await Task.WhenAll(ClaimAsync(4), ClaimAsync(4));

        var claimed = claims.SelectMany(batch => batch.SurveyIds).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(surveys.Order(), claimed.Order());
        Assert.Empty((await ClaimAsync(10)).SurveyIds);
        Assert.Equal(
            6L,
            await ScalarAsync(
                Factory,
                "SELECT count(*) FROM support.ticket_surveys WHERE tenant_id = @tenant AND locked_until > now()",
                ("tenant", scenario.TenantId)));
    }

    // Cada resultado se guarda con su auditoría de sistema y, después del commit, avisa el ticket por el hub. Sin subir la versión.
    [Fact]
    public async Task ProcessingAnnouncesTheTicket()
    {
        var scenario = await StartAsync(Factory);
        await ExecuteAsync(
            Factory, "UPDATE support.tenant_settings SET survey_enabled = true WHERE tenant_id = @tenant", ("tenant", scenario.TenantId));
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");

        await ProcessClaimedAsync();

        var email = Assert.Single(_email.Sent);
        Assert.Equal("ana@example.com", email.ToAddress);
        Assert.Equal(
            "Sent|Email|t",
            await TextAsync(
                Factory,
                "SELECT status || '|' || channel || '|' || (token_hash IS NOT NULL)::text FROM support.ticket_surveys WHERE ticket_id = @id",
                ("id", Id(resolved))));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySent, Id(resolved).ToString()));
        Assert.Contains(Id(resolved), _realtime.Tickets);
        Assert.Equal(Version(resolved), Version(await scenario.DetailAsync(Id(resolved))));
        var survey = Assert.Single((await scenario.DetailAsync(Id(resolved))).GetProperty("surveys").EnumerateArray());
        Assert.Equal(("Sent", "Email"), (survey.GetProperty("status").GetString(), survey.GetProperty("channel").GetString()));
    }

    // El tenant con la encuesta apagada (el default): «sin encuesta» con survey_disabled, auditada como tal.
    [Fact]
    public async Task ASurveyOffTenantIsSkippedAndAudited()
    {
        var scenario = await StartAsync(Factory);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");

        await ProcessClaimedAsync();

        Assert.Empty(_email.Sent);
        Assert.Equal(
            "Skipped|survey_disabled",
            await TextAsync(
                Factory, "SELECT status || '|' || reason_code FROM support.ticket_surveys WHERE ticket_id = @id", ("id", Id(resolved))));
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySkipped, Id(resolved).ToString()));
    }

    // Revisión del bloque C: el reclamo del lote cubre un envío, no el lote entero. Si el lote tardó más que su reclamo y otro worker
    // volvió a reclamar la encuesta, el primero ya no la manda: renovar el reclamo exige que siga siendo el suyo.
    [Fact]
    public async Task AStaleBatchClaimDoesNotProcessTheSurveyTwice()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        var first = await ClaimAsync(10);
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_surveys SET locked_until = now() - interval '1 second' WHERE ticket_id = @id",
            ("id", Id(resolved)));
        var second = await ClaimAsync(10);
        var surveyId = Assert.Single(first.SurveyIds);
        Assert.Equal(surveyId, Assert.Single(second.SurveyIds));

        await ProcessAsync(surveyId, first.ClaimedUntil);
        Assert.Empty(_email.Sent);
        await ProcessAsync(surveyId, second.ClaimedUntil);
        await ProcessAsync(surveyId, second.ClaimedUntil);

        Assert.Single(_email.Sent);
        Assert.Equal(1, await CountAuditEntriesAsync(Factory, SupportAuditActions.TicketSurveySent, Id(resolved).ToString()));
    }

    // Revisión del bloque C: cada encuesta renueva su reclamo justo antes de mandarse, así el reclamo cubre su propio envío aunque las
    // anteriores del lote hayan tardado.
    [Fact]
    public async Task EachSurveyRenewsItsClaimRightBeforeSending()
    {
        var scenario = await StartAsync(Factory);
        await EnableSurveyAsync(scenario);
        var resolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        var batch = await ClaimAsync(10);
        string? lockedAtSend = null;
        _email.OnSend = async _ => lockedAtSend = await TextAsync(
            Factory,
            "SELECT (locked_until > @claimed)::text FROM support.ticket_surveys WHERE ticket_id = @id",
            ("claimed", batch.ClaimedUntil),
            ("id", Id(resolved)));

        await ProcessAsync(Assert.Single(batch.SurveyIds), batch.ClaimedUntil);

        Assert.Equal("true", lockedAtSend);
        Assert.Single(_email.Sent);
        Assert.Equal(5, new Modules.Support.Infrastructure.SupportWorkerOptions().SurveyBatchSize);
    }

    private Task EnableSurveyAsync(SupportTicketScenario scenario) =>
        ExecuteAsync(
            Factory, "UPDATE support.tenant_settings SET survey_enabled = true WHERE tenant_id = @tenant", ("tenant", scenario.TenantId));

    private async Task<SupportSurveyBatch> ClaimAsync(int take)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().ClaimAsync(take, Ct);
    }

    private async Task ProcessAsync(Guid surveyId, DateTimeOffset claimedUntil)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().ProcessAsync(surveyId, claimedUntil, Ct);
    }

    private async Task ProcessClaimedAsync()
    {
        var batch = await ClaimAsync(10);
        foreach (var id in batch.SurveyIds)
        {
            await ProcessAsync(id, batch.ClaimedUntil);
        }
    }

    private async Task<IReadOnlyList<Guid>> SurveyIdsAsync(SupportTicketScenario scenario)
    {
        var ids = await TextAsync(
            Factory,
            "SELECT string_agg(id::text, ',') FROM support.ticket_surveys WHERE tenant_id = @tenant",
            ("tenant", scenario.TenantId));
        return ids!.Split(',').Select(id => Guid.Parse(id, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>El aviso del hub que anota el ticket en vez de publicarlo.</summary>
    private sealed class RecordingSupportRealtime : ISupportRealtime
    {
        private readonly ConcurrentQueue<Guid> _tickets = new();

        public IReadOnlyList<Guid> Tickets => [.. _tickets];

        public Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
        {
            _tickets.Enqueue(ticketId);
            return Task.CompletedTask;
        }
    }
}
