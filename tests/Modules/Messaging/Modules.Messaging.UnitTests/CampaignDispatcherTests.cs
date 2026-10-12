using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3: lo que hace el worker con cada destinatario, sin base y con Meta de mentira. Armado del mensaje, a quién se
/// le manda, clasificación de errores (reintentables, definitivos y los que detienen la campaña, decisión 15), el opt-out de 131050, la
/// congelación de la audiencia con sus motivos de omisión, la recuperación de leases y el cierre.</summary>
public sealed class CampaignDispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SendResult Sent(string wamid = "wamid.ok") => new(SendOutcome.Sent, wamid, null, null);

    private static SendResult Graph(int? code) => new(SendOutcome.GraphError, null, code, "title");

    private static SendResult Unconfirmed() => new(SendOutcome.Unconfirmed, null, null, null);

    [Fact]
    public async Task ItSendsTheTemplateToThePhoneWithTheCustomersNameAndTheFixedTextAndSettlesSent()
    {
        var bed = new DispatchBed();
        var recipient = bed.Recipient();
        bed.Bed.Meta.NextSend = Sent("wamid.1");

        await bed.Dispatcher.SendAsync(recipient, Ct);

        var send = Assert.Single(bed.Bed.Meta.TemplateSends);
        Assert.Equal(new SendToPhone("573001234567"), send.Target);
        Assert.Equal("token", send.Sender.AccessToken);
        Assert.Equal(("promo", "es", false), (send.Template.Name, send.Template.Language, send.Template.Named));
        Assert.Equal([new TemplateParameter("1", "Laura Gómez"), new TemplateParameter("2", "A1")], send.Template.Parameters);
        var prepared = Assert.Single(bed.Store.Prepared);
        Assert.Equal("Hola Laura Gómez, tu pedido A1 salió\n\nGracias", prepared.Text);
        using var details = JsonDocument.Parse(prepared.DetailsJson);
        Assert.Equal("MARKETING", details.RootElement.GetProperty("template").GetProperty("category").GetString());
        Assert.Equal("Laura Gómez", details.RootElement.GetProperty("template").GetProperty("parameters").GetProperty("1").GetString());
        var settled = Assert.Single(bed.Store.Sent);
        Assert.Equal("wamid.1", settled.Wamid);
        Assert.Equal(SendMessageHandler.CallbackPrefix + settled.Prepared.MessageId.ToString("D"), send.CallbackData);
        Assert.Equal(["prepare", "settle-sent"], bed.Store.Order);
    }

    [Fact]
    public async Task WithoutAPhoneItGoesToTheBsuid()
    {
        var bed = new DispatchBed();

        await bed.Dispatcher.SendAsync(bed.Recipient(waId: null, userId: "CO.9"), Ct);

        Assert.Equal(new SendToUserId("CO.9"), Assert.Single(bed.Bed.Meta.TemplateSends).Target);
    }

    [Fact]
    public async Task TheCustomersNameIsCleanedToMetasRulesBeforeItTravels()
    {
        var bed = new DispatchBed();
        var messy = "  Ana\n\tMaría       del    Mar  ";

        await bed.Dispatcher.SendAsync(bed.Recipient(name: messy), Ct);

        var value = Assert.Single(bed.Bed.Meta.TemplateSends).Template.Parameters[0].Value;
        Assert.Equal("Ana María del Mar", value);
        Assert.True(TemplateParameterRules.IsValid(value));
    }

    [Fact]
    public async Task ANameOverTheLimitIsTruncated()
    {
        var bed = new DispatchBed();

        await bed.Dispatcher.SendAsync(bed.Recipient(name: new string('a', 2000)), Ct);

        Assert.Equal(TemplateParameterRules.MaxValueLength, Assert.Single(bed.Bed.Meta.TemplateSends).Template.Parameters[0].Value.Length);
    }

    [Fact]
    public async Task ANamedTemplateSendsItsParameterNames()
    {
        var bed = new DispatchBed();
        bed.Bed.Templates.Catalog.Clear();
        bed.Bed.Templates.Catalog.Add(new MessageTemplateDto("promo", "es", "UTILITY", TemplateCatalog.Named, null, "Hola {{first_name}}", null, [], ["first_name"], true, null));
        var parameters = new Dictionary<string, CampaignParameter> { ["first_name"] = CampaignParameter.CustomerName };

        await bed.Dispatcher.SendAsync(bed.Recipient(parameters: parameters), Ct);

        var send = Assert.Single(bed.Bed.Meta.TemplateSends).Template;
        Assert.True(send.Named);
        Assert.Equal([new TemplateParameter("first_name", "Laura Gómez")], send.Parameters);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public async Task ARetriableMetaErrorIsRetriedWithABackoffOf30SecondsTimesTwoToTheN(int attempts, int seconds)
    {
        var bed = new DispatchBed();
        var recipient = bed.Recipient(attempts: attempts);
        bed.Bed.Meta.NextSend = Graph(130429);

        await bed.Dispatcher.SendAsync(recipient, Ct);

        var retry = Assert.Single(bed.Store.Retries);
        Assert.Equal(bed.Bed.Now.AddSeconds(seconds), retry.NextAttemptAt);
        Assert.Empty(bed.Store.Failed);
    }

    [Theory]
    [InlineData(130429)]
    [InlineData(131056)]
    public async Task AfterFiveAttemptsARetriableErrorFailsWithItsCode(int code)
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.NextSend = Graph(code);

        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 5), Ct);

        Assert.Empty(bed.Store.Retries);
        var failed = Assert.Single(bed.Store.Failed);
        Assert.Equal((code.ToString(System.Globalization.CultureInfo.InvariantCulture), (int?)code), (failed.Code, failed.GraphCode));
        Assert.Empty(bed.Store.Stops);
    }

    [Fact]
    public async Task AnUnconfirmedSendIsRetriedAndFailsAsUnconfirmedAfterFiveAttempts()
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.NextSend = Unconfirmed();

        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 2), Ct);
        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 5), Ct);

        var retry = Assert.Single(bed.Store.Retries);
        Assert.Equal(bed.Bed.Now.AddSeconds(60), retry.NextAttemptAt);
        var failed = Assert.Single(bed.Store.Failed);
        Assert.Equal((CampaignFailureCodes.Unconfirmed, (int?)MessageFailureReasons.Unconfirmed), (failed.Code, failed.GraphCode));
    }

    [Fact]
    public async Task ANetworkExceptionCountsAsUnconfirmed()
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.DuringSend = () => throw new HttpRequestException("boom");

        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 1), Ct);

        Assert.Single(bed.Store.Retries);
        Assert.Empty(bed.Store.Failed);
    }

    [Fact]
    public async Task ACancellationBeforeStartingSendsNothingAndLeavesTheLeaseToRecovery()
    {
        var bed = new DispatchBed();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bed.Dispatcher.SendAsync(bed.Recipient(), cancelled.Token));

        Assert.Empty(bed.Bed.Meta.TemplateSends);
        Assert.Empty(bed.Store.Order);
    }

    [Fact]
    public async Task OnceMetaIsCalledAShutdownDoesNotLoseItsAnswer()
    {
        var bed = new DispatchBed();
        using var shuttingDown = new CancellationTokenSource();
        bed.Bed.Meta.DuringSend = () => shuttingDown.Cancel();

        await bed.Dispatcher.SendAsync(bed.Recipient(), shuttingDown.Token);

        Assert.Single(bed.Store.Sent);
    }

    [Theory]
    [InlineData(131049)]
    [InlineData(131026)]
    [InlineData(132000)]
    [InlineData(132018)]
    [InlineData(100)]
    public async Task ADefinitiveMetaErrorFailsTheRecipientOnlyAndKeepsTheCampaignGoing(int code)
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.NextSend = Graph(code);

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        var failed = Assert.Single(bed.Store.Failed);
        Assert.Equal((code.ToString(System.Globalization.CultureInfo.InvariantCulture), (int?)code), (failed.Code, failed.GraphCode));
        Assert.NotNull(failed.Prepared);
        Assert.Empty(bed.Store.Stops);
        Assert.Empty(bed.Store.Retries);
        Assert.Empty(bed.Bed.Customers.OptedOut);
    }

    [Fact]
    public async Task AGraphErrorWithoutACodeIsDefinitiveToo()
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.NextSend = Graph(null);

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        var failed = Assert.Single(bed.Store.Failed);
        Assert.Equal(SendMessageHandler.UnknownGraphErrorCode, failed.GraphCode);
    }

    [Fact]
    public async Task AMarketingOptOutFromMetaFailsTheRecipientAndMarksTheCustomer()
    {
        var bed = new DispatchBed();
        var recipient = bed.Recipient();
        bed.Bed.Meta.NextSend = Graph(131050);

        await bed.Dispatcher.SendAsync(recipient, Ct);

        Assert.Equal("131050", Assert.Single(bed.Store.Failed).Code);
        Assert.Equal([recipient.CustomerId], bed.Bed.Customers.OptedOut);
        Assert.Empty(bed.Store.Stops);
    }

    [Theory]
    [InlineData(132001)]
    [InlineData(132015)]
    [InlineData(132016)]
    public async Task ATemplateProblemFailsTheRecipientAndStopsTheCampaign(int code)
    {
        var bed = new DispatchBed();
        var recipient = bed.Recipient();
        bed.Bed.Meta.NextSend = Graph(code);

        await bed.Dispatcher.SendAsync(recipient, Ct);

        Assert.Equal(code.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(bed.Store.Failed).Code);
        Assert.Equal([(bed.CampaignId, code.ToString(System.Globalization.CultureInfo.InvariantCulture))], bed.Store.Stops);
        Assert.Empty(bed.Bed.Connections.Reported);
        Assert.Equal(["prepare", "settle-failed", "stop"], bed.Store.Order);
    }

    [Theory]
    [InlineData(190, "token_expired")]
    [InlineData(133010, "number_unregistered")]
    public async Task RejectedCredentialsReportTheConnectionAndStopTheCampaign(int code, string reported)
    {
        var bed = new DispatchBed();
        bed.Bed.Meta.NextSend = Graph(code);

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        Assert.Equal([new RejectedReport(bed.Bed.TenantId, bed.Bed.ConnectionId, reported)], bed.Bed.Connections.Reported);
        Assert.Equal([(bed.CampaignId, code.ToString(System.Globalization.CultureInfo.InvariantCulture))], bed.Store.Stops);
    }

    [Fact]
    public async Task IfReportingTheConnectionFailsTheCampaignStopsAnyway()
    {
        var bed = new DispatchBed();
        bed.Bed.Connections.ReportFailure = new InvalidOperationException("integrations down");
        bed.Bed.Meta.NextSend = Graph(190);

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        Assert.Single(bed.Store.Stops);
    }

    [Fact]
    public async Task ATemplateGoneFromTheCatalogStopsTheCampaignWithoutCallingMeta()
    {
        var bed = new DispatchBed();
        bed.Bed.Templates.Catalog.Clear();
        var recipient = bed.Recipient();

        await bed.Dispatcher.SendAsync(recipient, Ct);

        Assert.Empty(bed.Bed.Meta.TemplateSends);
        Assert.Empty(bed.Store.Prepared);
        Assert.Equal([(bed.CampaignId, "132001")], bed.Store.Stops);
        Assert.Equal([(recipient, CampaignFailureCodes.CampaignFailed)], bed.Store.Skipped);
    }

    [Fact]
    public async Task VariablesThatNoLongerMatchTheTemplateStopTheCampaignToo()
    {
        var bed = new DispatchBed();
        bed.Bed.Templates.Catalog.Clear();
        bed.Bed.Templates.Catalog.Add(FakeMessageTemplates.Hello("promo", "es", "MARKETING") with { Parameters = ["1", "2", "3"] });

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        Assert.Equal([(bed.CampaignId, "132001")], bed.Store.Stops);
        Assert.Empty(bed.Bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task AConnectionThatIsNoLongerActiveStopsTheCampaign()
    {
        var bed = new DispatchBed();
        bed.Bed.Connections.Sender = null;
        var recipient = bed.Recipient();

        await bed.Dispatcher.SendAsync(recipient, Ct);

        Assert.Empty(bed.Bed.Meta.TemplateSends);
        Assert.Equal([(bed.CampaignId, CampaignFailureCodes.ConnectionUnavailable)], bed.Store.Stops);
        Assert.Equal([(recipient, CampaignFailureCodes.CampaignFailed)], bed.Store.Skipped);
    }

    [Fact]
    public async Task AFailureListingTheTemplatesIsRetriedNotFatal()
    {
        var bed = new DispatchBed();
        bed.Bed.Templates.Failure = new MessagingDomainException(MessagingErrorCodes.TemplatesUnavailable, "down");

        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 1), Ct);

        Assert.Single(bed.Store.Retries);
        Assert.Empty(bed.Store.Stops);
        Assert.Empty(bed.Bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task IfTheCampaignIsNoLongerSendingThereIsNoMetaCall()
    {
        var bed = new DispatchBed();
        bed.Store.Prepare = _ => null;

        await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);

        Assert.Empty(bed.Bed.Meta.TemplateSends);
        Assert.Equal(["prepare"], bed.Store.Order);
    }

    [Fact]
    public async Task ARetryReusesTheMessageItAlreadyHas()
    {
        var bed = new DispatchBed();
        var messageId = Guid.CreateVersion7();

        await bed.Dispatcher.SendAsync(bed.Recipient(attempts: 2, messageId: messageId), Ct);

        Assert.Equal(messageId, Assert.Single(bed.Store.Sent).Prepared.MessageId);
    }

    [Fact]
    public async Task TheRateIsKeptPerConnectionAcrossRecipients()
    {
        var bed = new DispatchBed(messagesPerSecond: 2);

        for (var index = 0; index < 6; index++)
        {
            await bed.Dispatcher.SendAsync(bed.Recipient(), Ct);
        }

        // Dos de golpe y los cuatro que faltan a 2 por segundo: dos segundos de reloj simulado.
        Assert.Equal(6, bed.Bed.Meta.TemplateSends.Count);
        Assert.InRange(bed.Time.Elapsed.TotalSeconds, 1.99, 2.01);
    }

    [Fact]
    public async Task TheTickActivatesFreezesSendsRecoversAndClosesInThatOrder()
    {
        var bed = new DispatchBed();
        bed.Store.Unfrozen.Add(new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "MARKETING", [Guid.CreateVersion7()], []));
        bed.Store.ToClaim.Add(bed.Recipient());

        await bed.Dispatcher.PrepareTickAsync(Ct);
        var claimed = await bed.Dispatcher.ClaimAsync(20, Ct);
        foreach (var recipient in claimed)
        {
            await bed.Dispatcher.SendAsync(recipient, Ct);
        }

        await bed.Dispatcher.FinishTickAsync(Ct);

        Assert.Equal(["activate", "list-unfrozen", "freeze", "claim", "prepare", "settle-sent", "recover", "close"], bed.Store.Order);
    }

    [Fact]
    public async Task ItClaimsTwiceTheRatePerSecondWithATwoMinuteLease()
    {
        var bed = new DispatchBed();

        await bed.Dispatcher.ClaimAsync(20, Ct);

        Assert.Equal((40, bed.Bed.Now, TimeSpan.FromMinutes(2)), bed.Store.ClaimCall);
    }

    [Fact]
    public async Task FreezingClassifiesEachContactWithItsSkipReasonByPrecedence()
    {
        var bed = new DispatchBed();
        var campaign = new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "MARKETING", [Guid.CreateVersion7()], []);
        bed.Store.Unfrozen.Add(campaign);
        var ok = DispatchBed.Contact("Ok", "+573001111111");
        var onlyBsuid = DispatchBed.Contact("Bsuid", phone: null, userId: "CO.7");
        var inactive = DispatchBed.Contact("Inactivo", active: false);
        var inactiveAndOptedOut = DispatchBed.Contact("Inactivo y sin promos", active: false, optOut: true);
        var unreachable = DispatchBed.Contact("Sin WhatsApp", phone: null);
        var unreachableAndOptedOut = DispatchBed.Contact("Sin WhatsApp ni promos", phone: null, optOut: true);
        var optedOut = DispatchBed.Contact("Sin promos", optOut: true);
        bed.Bed.Customers.Audience.AddRange([ok, onlyBsuid, inactive, inactiveAndOptedOut, unreachable, unreachableAndOptedOut, optedOut]);

        await bed.Dispatcher.PrepareTickAsync(Ct);

        var seeds = Assert.Single(bed.Store.Added).Batch.ToDictionary(seed => seed.CustomerName);
        Assert.Equal((CampaignRecipientStatus.Pending, null), (seeds["Ok"].Status, seeds["Ok"].FailureCode));
        Assert.Equal((CampaignRecipientStatus.Pending, null), (seeds["Bsuid"].Status, seeds["Bsuid"].FailureCode));
        Assert.Equal(("573001111111", "CO.7"), (seeds["Ok"].WaId, seeds["Bsuid"].UserId));
        Assert.Equal(CampaignFailureCodes.Inactive, seeds["Inactivo"].FailureCode);
        Assert.Equal(CampaignFailureCodes.Inactive, seeds["Inactivo y sin promos"].FailureCode);
        Assert.Equal(CampaignFailureCodes.Unreachable, seeds["Sin WhatsApp"].FailureCode);
        Assert.Equal(CampaignFailureCodes.Unreachable, seeds["Sin WhatsApp ni promos"].FailureCode);
        Assert.Equal(CampaignFailureCodes.OptedOut, seeds["Sin promos"].FailureCode);
        Assert.All(seeds.Values.Where(seed => seed.FailureCode is not null), seed => Assert.Equal(CampaignRecipientStatus.Skipped, seed.Status));
        Assert.Equal([bed.CampaignId], bed.Store.Frozen);
    }

    [Fact]
    public async Task AnOptOutOnlyExcludesFromMarketingTemplates()
    {
        var bed = new DispatchBed();
        bed.Store.Unfrozen.Add(new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "UTILITY", [Guid.CreateVersion7()], []));
        bed.Bed.Customers.Audience.Add(DispatchBed.Contact("Sin promos", optOut: true));

        await bed.Dispatcher.PrepareTickAsync(Ct);

        var seed = Assert.Single(Assert.Single(bed.Store.Added).Batch);
        Assert.Equal((CampaignRecipientStatus.Pending, null), (seed.Status, seed.FailureCode));
    }

    [Fact]
    public async Task TheAudienceIsWrittenInBatchesOf500AndFrozenAfterTheLastOne()
    {
        var bed = new DispatchBed();
        bed.Store.Unfrozen.Add(new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "MARKETING", [Guid.CreateVersion7()], []));
        bed.Bed.Customers.Audience.AddRange(Enumerable.Range(0, 1200).Select(index => DispatchBed.Contact($"Cliente {index}")));

        await bed.Dispatcher.PrepareTickAsync(Ct);

        Assert.Equal([500, 500, 200], bed.Store.Added.Select(call => call.Batch.Count));
        Assert.Equal(["activate", "list-unfrozen", "add-recipients", "add-recipients", "add-recipients", "freeze"], bed.Store.Order);
    }

    [Fact]
    public async Task AnEmptyAudienceIsFrozenWithoutRecipients()
    {
        var bed = new DispatchBed();
        bed.Store.Unfrozen.Add(new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "MARKETING", [Guid.CreateVersion7()], []));

        await bed.Dispatcher.PrepareTickAsync(Ct);

        Assert.Empty(bed.Store.Added);
        Assert.Equal([bed.CampaignId], bed.Store.Frozen);
    }

    [Fact]
    public async Task ALongNameIsStoredTruncatedTo256()
    {
        var bed = new DispatchBed();
        bed.Store.Unfrozen.Add(new CampaignFreezeTarget(bed.CampaignId, bed.Bed.TenantId, "MARKETING", [Guid.CreateVersion7()], []));
        bed.Bed.Customers.Audience.Add(DispatchBed.Contact(new string('n', 400)));

        await bed.Dispatcher.PrepareTickAsync(Ct);

        Assert.Equal(256, Assert.Single(Assert.Single(bed.Store.Added).Batch).CustomerName.Length);
    }
}
