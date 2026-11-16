using System.Net;
using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §5.2 and plan N13: every Support write and the thread follower announce <c>supportTicketChanged { ticketId }</c> after
/// their commit, on the ticket's topic and on the inbox. A write that fails announces nothing, and a broken publisher never fails the
/// write. One host for the class with the recording publisher, the reply doubles and the fake captcha; the follower is off and runs by
/// hand. Each test seeds its own tenant and only reads that tenant's notices. Final review S3: an opening that records its delivery in
/// <c>ticket_access</c> (a ticket with somewhere to send it) announces once more after that second save, and so does «Reenviar PIN», so
/// other screens never keep a stale <c>consultAccess</c>.
/// </summary>
public sealed class SupportRealtimeNoticesApiTests(SupportRealtimeNoticesHost host) : IClassFixture<SupportRealtimeNoticesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    private RecordingRealtimePublisher Publisher => host.Publisher;

    [Fact]
    public async Task CreatingATicketAnnouncesIt()
    {
        var scenario = await StartAsync(Factory);

        var ticket = await scenario.CreateAsync();

        // Ana has an email: the creation, then the recorded delivery of the opening.
        AssertAnnounced(Publisher.Of(scenario.TenantId), Id(ticket), Id(ticket));
    }

    // Fase 4a (plan G1): sin a dónde mandar la apertura, un solo aviso.
    [Fact]
    public async Task CreatingAnAnonymousManualTicketAnnouncesIt()
    {
        var scenario = await StartAsync(Factory);
        var body = Body(await scenario.TypeIdAsync("Queja"), replyChannel: "Portal");
        body["requester"] = null;
        body["anonymous"] = true;

        var ticket = await scenario.CreateAsync(body);

        AssertAnnounced(Publisher.Of(scenario.TenantId), Id(ticket));
    }

    [Fact]
    public async Task CreatingFromAConversationAnnouncesIt()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);

        var ticket = await scenario.CreateFromConversationAsync(conversation);

        // The opening goes by the thread: the creation, then its recorded delivery.
        AssertAnnounced(Publisher.Of(scenario.TenantId), Id(ticket), Id(ticket));
    }

    [Fact]
    public async Task FilingThePublicFormAnnouncesIt()
    {
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(Factory, tenant);
        using var visitor = VisitorClient(Factory);

        var response = await PostJsonAsync(visitor, PublicUrl(slug, "/tickets"), AnonymousBody(await FirstTypeIdAsync(Factory, tenant)));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var ticketId = Guid.Parse((await TextAsync(
            Factory, "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant LIMIT 1", ("tenant", Guid.Parse(tenant))))!);
        // Anonymous: nowhere to send the opening, so nothing is recorded and there is no second notice.
        AssertAnnounced(Publisher.Of(Guid.Parse(tenant)), ticketId);
    }

    [Fact]
    public async Task FilingANamedTicketAnnouncesItAndThenItsRecordedOpening()
    {
        var (tenantId, _, ticketId) = await FileNamedAsync();

        AssertAnnounced(Publisher.Of(tenantId), ticketId, ticketId);
        Assert.Equal("Email", await TextAsync(
            Factory, "SELECT last_sent_channel FROM support.ticket_access WHERE ticket_id = @id", ("id", ticketId)));
    }

    [Fact]
    public async Task ResendingThePinAnnouncesTheTicket()
    {
        var (tenantId, slug, ticketId) = await FileNamedAsync();
        // No clock double in this host: the cooldown is spent by moving the last attempt back.
        await ExecuteAsync(
            Factory,
            "UPDATE support.ticket_access SET last_sent_at = last_sent_at - make_interval(mins => 11) WHERE ticket_id = @id",
            ("id", ticketId));
        var before = Publisher.Of(tenantId).Count;
        using var visitor = VisitorClient(Factory);

        var response = await PostJsonAsync(
            visitor, PublicUrl(slug, "/tickets/resend-pin"), new { number = "SOP-000001", turnstileToken = FakeSupportCaptcha.ValidToken });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertAnnounced(Since(tenantId, before), ticketId);
    }

    [Fact]
    public async Task StatusAssigneeTypeAndCustomerAnnounceTheTicket()
    {
        var scenario = await StartAsync(Factory);
        var customer = await SeedCustomerAsync(Factory, scenario.TenantId, "Novasteel SAS");
        var ticket = await scenario.CreateAsync();
        var id = Id(ticket);
        var before = Publisher.Of(scenario.TenantId).Count;

        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "status", new ChangeTicketStatusRequest("Open"), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "assignee", new AssignTicketRequest(scenario.CallerMemberId), 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "type", new ChangeTicketTypeRequest(await scenario.TypeIdAsync("Reclamo")), 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "customer", new LinkTicketCustomerRequest(customer), 4)).StatusCode);

        AssertAnnounced(Since(scenario.TenantId, before), id, id, id, id);
    }

    [Fact]
    public async Task PinReleaseAndFollowAnnounceTheTicket()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var older = await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddDays(-2));
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        var id = Id(ticket);
        var before = Publisher.Of(scenario.TenantId).Count;

        var pinned = await scenario.PostActionAsync($"tickets/{id}/messages/{older}/pin", null, Version(ticket));
        Assert.True(pinned.StatusCode == HttpStatusCode.OK, await BodyAsync(pinned));
        var released = await scenario.PostActionAsync($"tickets/{id}/messages/{older}/release", null, Version(await JsonAsync(pinned)));
        Assert.True(released.StatusCode == HttpStatusCode.OK, await BodyAsync(released));
        var unfollowed = await scenario.ActAsync(id, "follow-thread", new { follow = false }, Version(await JsonAsync(released)));
        Assert.True(unfollowed.StatusCode == HttpStatusCode.OK, await BodyAsync(unfollowed));

        AssertAnnounced(Since(scenario.TenantId, before), id, id, id);
    }

    [Fact]
    public async Task AReplyAnnouncesTheTicket()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();
        var before = Publisher.Of(scenario.TenantId).Count;

        var response = await scenario.PostActionAsync(
            $"tickets/{Id(ticket)}/replies", new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Te enviamos la factura." }, Version(ticket));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        AssertAnnounced(Since(scenario.TenantId, before), Id(ticket));
    }

    [Fact]
    public async Task ANoteAnnouncesTheTicket()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();
        var before = Publisher.Of(scenario.TenantId).Count;

        var response = await scenario.PostActionAsync(
            $"tickets/{Id(ticket)}/notes", new { clientId = Guid.CreateVersion7(), text = "Llamar el lunes." }, null);

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        AssertAnnounced(Since(scenario.TenantId, before), Id(ticket));
    }

    [Fact]
    public async Task ClassifyingAnnouncesTheChosenTicketAndDismissingAnnouncesEveryCandidate()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var chosen = await scenario.CreateFromConversationAsync(conversation);
        var other = await scenario.CreateFromConversationAsync(conversation, "Otro caso");
        var classified = await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        await FollowAsync();
        var current = await scenario.DetailAsync(Id(chosen));
        var beforeAssign = Publisher.Of(scenario.TenantId).Count;

        var assigned = await scenario.PostActionAsync(
            $"conversations/{conversation}/unclassified/{classified}/assign", new { ticketId = Id(chosen), version = Version(current) }, null);

        Assert.True(assigned.StatusCode == HttpStatusCode.OK, await BodyAsync(assigned));
        AssertAnnounced(Since(scenario.TenantId, beforeAssign), Id(chosen));

        var dismissed = await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(2));
        await FollowAsync();
        var beforeDismiss = Publisher.Of(scenario.TenantId).Count;

        var response = await scenario.PostActionAsync($"conversations/{conversation}/unclassified/{dismissed}/dismiss", null, null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var notices = Since(scenario.TenantId, beforeDismiss);
        Assert.Equal(4, notices.Count);
        Assert.Equal(new[] { Id(chosen), Id(other) }.Order(), notices.Select(notice => notice.TicketId).Distinct().Order());
        foreach (var id in new[] { Id(chosen), Id(other) })
        {
            Assert.Contains(notices, notice => notice.TicketId == id && notice.Topic == SupportRealtimeTopics.Ticket(id));
            Assert.Contains(notices, notice => notice.TicketId == id && notice.Topic == SupportRealtimeTopics.Inbox);
        }
    }

    [Fact]
    public async Task AFailedWriteAnnouncesNothing()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();
        var opened = await scenario.MoveAsync(ticket, "Open");
        var before = Publisher.Of(scenario.TenantId).Count;

        var stale = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Resolved"), Version(ticket));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Empty(Since(scenario.TenantId, before));

        var closed = await scenario.MoveAsync(await scenario.MoveAsync(opened, "Resolved"), "Closed");
        var beforeNotEditable = Publisher.Of(scenario.TenantId).Count;

        var notEditable = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(scenario.CallerMemberId), Version(closed));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notEditable.StatusCode);
        Assert.Equal(SupportErrorCodes.NotEditable, await CodeOfAsync(notEditable));
        Assert.Empty(Since(scenario.TenantId, beforeNotEditable));
    }

    // Fase 4a (plan G7): «Asignármelo» avisa sólo si cambió algo.
    [Fact]
    public async Task TakingAnnouncesTheTicket()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();
        var before = Publisher.Of(scenario.TenantId).Count;

        var taken = await scenario.PostActionAsync($"tickets/{Id(ticket)}/take", null, Version(ticket));

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        AssertAnnounced(Since(scenario.TenantId, before), Id(ticket));
        var beforeAgain = Publisher.Of(scenario.TenantId).Count;

        var again = await scenario.PostActionAsync($"tickets/{Id(ticket)}/take", null, Version(await JsonAsync(taken)));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(Since(scenario.TenantId, beforeAgain));
    }

    [Fact]
    public async Task TheWorkerAnnouncesALinkedInboundWithoutBumpingTheVersion()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var ticket = await scenario.CreateFromConversationAsync(conversation);
        await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        var before = Publisher.Of(scenario.TenantId).Count;

        await FollowAsync();

        AssertAnnounced(Since(scenario.TenantId, before), Id(ticket));
        Assert.Equal(Version(ticket), Version(await scenario.DetailAsync(Id(ticket))));
    }

    [Fact]
    public async Task TheWorkerAnnouncesEveryCandidateOfANewUnclassifiedRow()
    {
        var scenario = await StartAsync(Factory);
        var conversation = await SeedConversationAsync(Factory, scenario.TenantId);
        var first = await scenario.CreateFromConversationAsync(conversation);
        var second = await scenario.CreateFromConversationAsync(conversation, "Otro caso");
        await SeedMessageAsync(Factory, scenario.TenantId, conversation, DateTimeOffset.UtcNow.AddMinutes(1));
        var before = Publisher.Of(scenario.TenantId).Count;

        await FollowAsync();

        var notices = Since(scenario.TenantId, before);
        Assert.Equal(4, notices.Count);
        Assert.Equal(new[] { Id(first), Id(second) }.Order(), notices.Select(notice => notice.TicketId).Distinct().Order());
    }

    [Fact]
    public async Task APublisherFailureDoesNotFailTheWrite()
    {
        var scenario = await StartAsync(Factory);
        var ticket = await scenario.CreateAsync();
        Publisher.Fail = true;
        try
        {
            var response = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Open"), Version(ticket));

            Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
            Assert.Equal("Open", (await scenario.DetailAsync(Id(ticket))).GetProperty("status").GetString());
            // The notifier itself swallows the failure (plan Task 2): it is what every handler calls after its commit.
            await using var scope = Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISupportRealtime>().TicketChangedAsync(scenario.TenantId, Id(ticket), Ct);
        }
        finally
        {
            Publisher.Fail = false;
        }
    }

    /// <summary>Exactly two notices per ticket, in order: its own topic first, then the inbox.</summary>
    private static void AssertAnnounced(IReadOnlyList<RecordingRealtimePublisher.Notice> notices, params Guid[] ticketIds)
    {
        Assert.Equal(ticketIds.Length * 2, notices.Count);
        for (var i = 0; i < ticketIds.Length; i++)
        {
            var own = notices[i * 2];
            var inbox = notices[(i * 2) + 1];
            Assert.All([own, inbox], notice =>
            {
                Assert.Equal(SupportRealtimeEvents.TicketChanged, notice.Kind);
                Assert.Equal(ticketIds[i], notice.TicketId);
            });
            Assert.Equal(SupportRealtimeTopics.Ticket(ticketIds[i]), own.Topic);
            Assert.Equal(SupportRealtimeTopics.Inbox, inbox.Topic);
        }
    }

    /// <summary>A named public ticket with an email (SOP-000001 of a fresh tenant): its opening goes out by email.</summary>
    private async Task<(Guid TenantId, string Slug, Guid TicketId)> FileNamedAsync()
    {
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(Factory, tenant);
        using var visitor = VisitorClient(Factory);
        var response = await PostJsonAsync(
            visitor,
            PublicUrl(slug, "/tickets"),
            NamedBody(await FirstTypeIdAsync(Factory, tenant), await AnyCityIdAsync(Factory), $"avisos-{Guid.NewGuid():N}@example.com"));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var ticketId = Guid.Parse((await TextAsync(
            Factory, "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant LIMIT 1", ("tenant", Guid.Parse(tenant))))!);
        return (Guid.Parse(tenant), slug, ticketId);
    }

    private IReadOnlyList<RecordingRealtimePublisher.Notice> Since(Guid tenantId, int count) =>
        [.. Publisher.Of(tenantId).Skip(count)];

    private async Task FollowAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
    }
}

/// <summary>The host of <see cref="SupportRealtimeNoticesApiTests"/>: the recording publisher in place of the NOTIFY one, the reply
/// doubles (no Meta, no mail), the fake captcha for the public form and the public form settings. The follower is off.</summary>
public sealed class SupportRealtimeNoticesHost : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    internal RecordingRealtimePublisher Publisher { get; } = new();

    internal QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        _factory = new QepApiFactory(
            _database.GetConnectionString(),
            services =>
            {
                RemoveThreadFollowWorker(services);
                services.RemoveAll<IRealtimePublisher>();
                services.AddSingleton<IRealtimePublisher>(Publisher);
                services.RemoveAll<ISupportReplyChannels>();
                services.AddSingleton<ISupportReplyChannels>(new FakeSupportReplyChannels());
                services.RemoveAll<ISupportReplyEmail>();
                services.AddSingleton<ISupportReplyEmail>(new RecordingSupportReplyEmail());
                services.RemoveAll<ISupportCaptcha>();
                services.AddSingleton<ISupportCaptcha>(new FakeSupportCaptcha());
                services.RemoveAll<ISupportOpeningEmail>();
                services.AddSingleton<ISupportOpeningEmail>(new RecordingSupportOpeningEmail());
            },
            SupportPublicHarness.Settings(realAuthentication: false));
    }

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
}
