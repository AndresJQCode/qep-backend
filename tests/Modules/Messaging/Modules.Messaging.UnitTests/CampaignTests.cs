using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.1: el agregado <see cref="Campaign"/> con su máquina de estados (Draft → Scheduled/Sending, y de ahí
/// Cancelled) y las reglas de entrada que el validador repite para dar el campo exacto: nombre 1–120, audiencia no vacía, hasta 500
/// clientes sueltos, <c>scheduledAt</c> a futuro y a no más de 30 días, y fuentes de variable <c>text</c> o <c>customerName</c>.</summary>
public sealed class CampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    private static readonly IReadOnlyDictionary<string, CampaignParameter> OneText =
        new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.Text("Hola") };

    private static Campaign Draft(
        string name = "Promo de octubre",
        IReadOnlyCollection<Guid>? tags = null,
        IReadOnlyCollection<Guid>? customers = null,
        DateTimeOffset? scheduledAt = null,
        IReadOnlyDictionary<string, CampaignParameter>? parameters = null) =>
        Campaign.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), name, "promo", "es", "MARKETING",
            parameters ?? OneText, tags ?? [Guid.CreateVersion7()], customers ?? [], scheduledAt, Member, Now);

    [Fact]
    public void CreateStartsAsADraftWithoutDatesAndAtVersionOne()
    {
        var campaign = Draft(name: "  Promo  ");

        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal("Promo", campaign.Name);
        Assert.Equal(("promo", "es", "MARKETING"), (campaign.TemplateName, campaign.TemplateLanguage, campaign.TemplateCategory));
        Assert.Null(campaign.StartedAt);
        Assert.Null(campaign.CompletedAt);
        Assert.Null(campaign.FailureReason);
        Assert.Equal(Member, campaign.CreatedByMemberId);
        Assert.Equal(1, campaign.Version);
        Assert.Equal(Now, campaign.CreatedAt);
        Assert.Equal(Now, campaign.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRefused(string name)
    {
        var error = Assert.Throws<MessagingDomainException>(() => Draft(name: name));
        Assert.Equal(MessagingErrorCodes.CampaignInvalid, error.Code);
    }

    [Fact]
    public void TheNameTakesAtMost120Characters()
    {
        Assert.Equal(120, Draft(name: new string('a', 120)).Name.Length);
        Assert.Throws<MessagingDomainException>(() => Draft(name: new string('a', 121)));
    }

    [Fact]
    public void AnEmptyAudienceIsRefused()
    {
        Assert.Throws<MessagingDomainException>(() => Draft(tags: [], customers: []));
    }

    [Fact]
    public void ALooseCustomerAloneIsAnAudienceAndTheCapIs500()
    {
        Assert.Single(Draft(tags: [], customers: [Guid.CreateVersion7()]).AudienceCustomerIds);
        Assert.Equal(500, Draft(tags: [], customers: [.. Enumerable.Range(0, 500).Select(_ => Guid.CreateVersion7())]).AudienceCustomerIds.Count);
        Assert.Throws<MessagingDomainException>(() => Draft(tags: [], customers: [.. Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7())]));
    }

    [Fact]
    public void TheAudienceIdsAreDeduplicated()
    {
        var tag = Guid.CreateVersion7();
        var campaign = Draft(tags: [tag, tag], customers: []);

        Assert.Equal([tag], campaign.AudienceTagIds);
    }

    [Fact]
    public void AScheduleHasToBeInTheFutureAndWithin30Days()
    {
        Assert.Equal(Now.AddHours(1), Draft(scheduledAt: Now.AddHours(1)).ScheduledAt);
        Assert.Equal(Now.AddDays(30), Draft(scheduledAt: Now.AddDays(30)).ScheduledAt);
        Assert.Throws<MessagingDomainException>(() => Draft(scheduledAt: Now));
        Assert.Throws<MessagingDomainException>(() => Draft(scheduledAt: Now.AddMinutes(-1)));
        Assert.Throws<MessagingDomainException>(() => Draft(scheduledAt: Now.AddDays(30).AddSeconds(1)));
    }

    [Fact]
    public void AVariableTakesATextWithValueOrTheCustomersName()
    {
        var campaign = Draft(parameters: new Dictionary<string, CampaignParameter>
        {
            ["1"] = CampaignParameter.CustomerName,
            ["promo"] = CampaignParameter.Text("20%"),
        });

        Assert.Equal(CampaignParameterSource.CustomerName, campaign.Parameters["1"].Source);
        Assert.Null(campaign.Parameters["1"].Value);
        Assert.Equal("20%", campaign.Parameters["promo"].Value);
        Assert.Throws<MessagingDomainException>(() => Draft(parameters: new Dictionary<string, CampaignParameter> { ["1"] = new(CampaignParameterSource.Text, null) }));
    }

    // El saludo se calcula al despachar: no tiene valor, y el cable sólo dice de dónde sale.
    [Fact]
    public void AVariableCanComeFromTheGreetingWithoutAValue()
    {
        var campaign = Draft(parameters: new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.Greeting });

        Assert.Equal(CampaignParameterSource.Greeting, campaign.Parameters["1"].Source);
        Assert.Null(campaign.Parameters["1"].Value);
    }

    [Theory]
    [InlineData("text", CampaignParameterSource.Text)]
    [InlineData("customerName", CampaignParameterSource.CustomerName)]
    [InlineData("greeting", CampaignParameterSource.Greeting)]
    public void EverySourceReadsAndWritesItsWireName(string wire, CampaignParameterSource source)
    {
        Assert.True(CampaignParameter.TryParseSource(wire, out var parsed));
        Assert.Equal(source, parsed);
        Assert.Equal(wire, new CampaignParameter(source, null).SourceWire);
    }

    [Fact]
    public void LaunchingWithoutADateSendsNowAndStampsStartedAt()
    {
        var campaign = Draft();

        campaign.Launch(Now.AddMinutes(5));

        Assert.Equal(CampaignStatus.Sending, campaign.Status);
        Assert.Equal(Now.AddMinutes(5), campaign.StartedAt);
        Assert.Equal(2, campaign.Version);
    }

    [Fact]
    public void LaunchingWithAFutureDateSchedulesIt()
    {
        var campaign = Draft(scheduledAt: Now.AddHours(2));

        campaign.Launch(Now.AddMinutes(1));

        Assert.Equal(CampaignStatus.Scheduled, campaign.Status);
        Assert.Null(campaign.StartedAt);
        Assert.Equal(2, campaign.Version);
    }

    [Fact]
    public void ADateThatAlreadyPassedCannotBeLaunched()
    {
        var campaign = Draft(scheduledAt: Now.AddHours(1));

        var error = Assert.Throws<MessagingDomainException>(() => campaign.Launch(Now.AddHours(2)));

        Assert.Equal(MessagingErrorCodes.CampaignInvalid, error.Code);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
    }

    [Fact]
    public void OnlyADraftCanBeEditedOrLaunched()
    {
        var campaign = Draft();
        campaign.Launch(Now);

        Assert.False(campaign.IsEditable);
        Assert.Throws<InvalidOperationException>(() => campaign.Launch(Now));
        Assert.Throws<InvalidOperationException>(() => campaign.Update(
            "otro", Guid.CreateVersion7(), "promo", "es", "MARKETING", OneText, [Guid.CreateVersion7()], [], null, Now));
    }

    [Fact]
    public void UpdateReplacesTheInputAndBumpsTheVersion()
    {
        var campaign = Draft();
        var connection = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();

        campaign.Update("Otra", connection, "seguimiento", "pt_BR", "UTILITY", OneText, [], [customer], Now.AddDays(1), Now.AddMinutes(3));

        Assert.Equal(("Otra", connection, "seguimiento", "pt_BR", "UTILITY"), (campaign.Name, campaign.ConnectionId, campaign.TemplateName, campaign.TemplateLanguage, campaign.TemplateCategory));
        Assert.Equal([customer], campaign.AudienceCustomerIds);
        Assert.Empty(campaign.AudienceTagIds);
        Assert.Equal(Now.AddDays(1), campaign.ScheduledAt);
        Assert.Equal(Now.AddMinutes(3), campaign.UpdatedAt);
        Assert.Equal(2, campaign.Version);
    }

    [Fact]
    public void AScheduledOrSendingCampaignCanBeCancelledAndKeepsItsCompletionTime()
    {
        var scheduled = Draft(scheduledAt: Now.AddHours(1));
        scheduled.Launch(Now);
        var sending = Draft();
        sending.Launch(Now);

        scheduled.Cancel(Now.AddMinutes(10));
        sending.Cancel(Now.AddMinutes(10));

        Assert.All([scheduled, sending], campaign =>
        {
            Assert.Equal(CampaignStatus.Cancelled, campaign.Status);
            Assert.Equal(Now.AddMinutes(10), campaign.CompletedAt);
            Assert.Equal(3, campaign.Version);
        });
        Assert.False(scheduled.IsCancellable);
    }

    [Fact]
    public void RetryReopensAFinishedCampaignClearsItsStopAndCountsTheRetry()
    {
        var completed = Draft();
        completed.Launch(Now);
        CampaignStates.Finish(completed, CampaignStatus.Completed);
        var failed = Draft();
        failed.Launch(Now);
        CampaignStates.Finish(failed, CampaignStatus.Failed, "132015");

        completed.Retry("MARKETING", Now.AddHours(1));
        failed.Retry("UTILITY", Now.AddHours(1));
        CampaignStates.Finish(completed, CampaignStatus.Completed);
        completed.Retry("MARKETING", Now.AddHours(2));

        Assert.Equal((CampaignStatus.Sending, 2, 4L), (completed.Status, completed.RetryCount, completed.Version));
        Assert.Equal((CampaignStatus.Sending, 1, "UTILITY"), (failed.Status, failed.RetryCount, failed.TemplateCategory));
        Assert.All([completed, failed], campaign =>
        {
            Assert.Null(campaign.CompletedAt);
            Assert.Null(campaign.FailureCode);
            Assert.Null(campaign.FailureReason);
            Assert.Equal(Now, campaign.StartedAt);
        });
    }

    [Fact]
    public void OnlyACompletedOrFailedCampaignCanBeRetried()
    {
        var draft = Draft();
        Assert.False(draft.IsRetriable);
        Assert.Throws<InvalidOperationException>(() => draft.Retry("MARKETING", Now));

        draft.Launch(Now);
        Assert.Throws<InvalidOperationException>(() => draft.Retry("MARKETING", Now));

        draft.Cancel(Now);
        Assert.False(draft.IsRetriable);
        Assert.Throws<InvalidOperationException>(() => draft.Retry("MARKETING", Now));
    }

    [Fact]
    public void ADraftOrACancelledCampaignCannotBeCancelled()
    {
        var draft = Draft();
        Assert.False(draft.IsCancellable);
        Assert.Throws<InvalidOperationException>(() => draft.Cancel(Now));

        draft.Launch(Now);
        draft.Cancel(Now);
        Assert.Throws<InvalidOperationException>(() => draft.Cancel(Now));
    }
}
