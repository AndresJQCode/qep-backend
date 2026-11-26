using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// Fase 4b, Tarea 11 (H16, H17, owner 2026-11-16, Q-2): por qué canal sale la encuesta de cada resolución y qué queda en la fila. Dobles
/// de los puertos: lo que hace Mensajería o el correo con el envío lo amarran sus propias pruebas. Lo que se guarda, la auditoría y el aviso
/// los amarra <c>SupportSurveyApiTests</c>.
/// </summary>
public sealed class SupportSurveySenderTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Creator = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid ConversationId = Guid.Parse("0193a000-0000-7000-8000-0000000000c1");
    private static readonly Guid ConnectionId = Guid.Parse("0193a000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Received.AddDays(1);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private readonly FakeChannels _channels = new();
    private readonly FakeSurveyEmail _email = new();
    private readonly FakeMembers _members = new();

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];

    private SupportSurveySender Sender() =>
        new(
            null!, null!, null!, new NullModules(), new FakeTenants(), _channels, _email, new FakePublicOptions(), null!,
            new SupportSystemAuthor(_members, null!), null!, null!, null!, new FixedClock(Now));

    private static SupportTenantSettings Settings(bool surveyEnabled)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Received);
        settings.ChangeSurvey(surveyEnabled, 5, Received);
        return settings;
    }

    private static SupportTicketSurvey Resolve(SupportTicket ticket)
    {
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Creator, Received.AddHours(2), Clock);
        return ticket.Surveys.Single();
    }

    private static SupportTicket Manual(SupportReplyChannel channel = SupportReplyChannel.Email, TicketRequester? requester = null) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 7, Petition, "No me llegó la factura", "Pedí la factura y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, channel, null, requester ?? Person, null, Creator, Received, Clock);

    private static SupportTicket FromConversation(string channel, TicketRequester? requester = null) =>
        SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 7, Petition, "Pedido sin llegar", "No ha llegado.", SupportTicketPriority.Medium,
            new SupportConversationRef(ConversationId, channel), null, requester ?? Person, null, Creator, Received, Clock);

    private void WhatsAppThread(bool templateApproved = true)
    {
        _channels.Threads[ConversationId] = new SupportThreadStatus(ConversationId, "WhatsApp", false, null, ConnectionId);
        if (templateApproved)
        {
            _channels.ApprovedSurveyConnections.Add(ConnectionId);
        }

        _members.Active(Creator);
    }

    [Fact]
    public async Task ASurveyOffTenantSkipsWithSurveyDisabled()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread();

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: false), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Skipped, "survey_disabled"), (survey.Status, survey.ReasonCode));
        Assert.Empty(_channels.SurveySends);
        Assert.Empty(_email.Sent);
    }

    // Revisión del bloque C: el ticket se reabrió antes de que el worker llegara a la encuesta. Esa resolución ya no se juzga (H21): no
    // se manda nada y queda «sin encuesta» con ticket_reopened. Igual si ya se volvió a resolver: la encuesta vieja es de otro ciclo.
    [Fact]
    public async Task AReopenedTicketIsSkippedWithTicketReopened()
    {
        var reopened = FromConversation("WhatsApp");
        var first = Resolve(reopened);
        reopened.ChangeStatus(SupportTicketStatus.Open, Creator, Received.AddHours(3), Clock);
        var resolvedAgain = FromConversation("WhatsApp");
        var old = Resolve(resolvedAgain);
        resolvedAgain.ChangeStatus(SupportTicketStatus.Open, Creator, Received.AddHours(3), Clock);
        resolvedAgain.ChangeStatus(SupportTicketStatus.Resolved, Creator, Received.AddHours(4), Clock);
        WhatsAppThread();

        await Sender().DecideAsync(first, reopened, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);
        await Sender().DecideAsync(old, resolvedAgain, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Skipped, SupportChannelReasons.TicketReopened), (first.Status, first.ReasonCode));
        Assert.Equal((SupportSurveyStatus.Skipped, "ticket_reopened"), (old.Status, old.ReasonCode));
        Assert.Contains(SupportChannelReasons.TicketReopened, SupportChannelReasons.All);
        Assert.Empty(_channels.SurveySends);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task AnAnonymousTicketIsPublishedOnThePortal()
    {
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 7, Petition, "Queja anónima", "Sin datos.", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Portal, null, null, null, Creator, Received, Clock, anonymous: true);
        var survey = Resolve(ticket);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Sent, (SupportSurveyChannel?)SupportSurveyChannel.Portal, (Guid?)null, Now),
            (survey.Status, survey.Channel, survey.MessageId, survey.SentAt));
        Assert.Empty(_channels.SurveySends);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task AWhatsAppTicketSendsTheTemplateWithTheSurveyAsClientId()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread();
        var message = Guid.CreateVersion7();
        _channels.OnSurvey = send => new SupportChannelSendResult.Sent(message, send.ConversationId);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        var send = Assert.Single(_channels.SurveySends);
        Assert.Equal((ConversationId, survey.Id, Creator, "Ana María Gómez", "SOP-000007"),
            (send.ConversationId, send.ClientId, send.AuthorMemberId, send.RecipientName, send.TicketNumber));
        Assert.Equal((SupportSurveyStatus.Sent, (SupportSurveyChannel?)SupportSurveyChannel.WhatsApp, (Guid?)message),
            (survey.Status, survey.Channel, survey.MessageId));
        Assert.Null(survey.TokenHash);
    }

    [Fact]
    public async Task WithoutTheTemplateTheTicketIsWithoutSurvey()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread(templateApproved: false);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Skipped, "template_not_approved", (SupportSurveyChannel?)SupportSurveyChannel.WhatsApp),
            (survey.Status, survey.ReasonCode, survey.Channel));
        Assert.Empty(_channels.SurveySends);
    }

    // Q-2: nunca por Instagram; con correo en la foto, el enlace por correo.
    [Fact]
    public async Task AnInstagramTicketWithAnEmailGetsTheEmailLink()
    {
        var ticket = FromConversation("Instagram");
        var survey = Resolve(ticket);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Sent, (SupportSurveyChannel?)SupportSurveyChannel.Email), (survey.Status, survey.Channel));
        Assert.Equal("ana@example.com", Assert.Single(_email.Sent).ToAddress);
        Assert.Empty(_channels.SurveySends);
        Assert.Empty(_channels.ThreadLookups);
    }

    [Fact]
    public async Task AnInstagramTicketWithoutEmailIsPortalOnly()
    {
        var ticket = FromConversation("Instagram", Person with { Email = null });
        var survey = Resolve(ticket);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Sent, (SupportSurveyChannel?)SupportSurveyChannel.Portal), (survey.Status, survey.Channel));
        Assert.Empty(_email.Sent);
        Assert.Empty(_channels.SurveySends);
        Assert.Empty(_channels.ThreadLookups);
    }

    // H19: el enlace lleva un token de 43 caracteres base64url y la fila guarda sólo su SHA-256.
    [Fact]
    public async Task AnEmailTicketGetsTheEmailWithATokenAndOnlyItsHashIsStored()
    {
        var ticket = Manual();
        var survey = Resolve(ticket);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        var email = Assert.Single(_email.Sent);
        Assert.Equal(("ana@example.com", "Tienda de Ana", "SOP-000007", "No me llegó la factura"),
            (email.ToAddress, email.TenantName, email.TicketNumber, email.TicketSubject));
        var url = email.RatingUrl.AbsoluteUri;
        Assert.StartsWith("https://soporte.example.com/tienda/calificar?token=", url, StringComparison.Ordinal);
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..];
        Assert.True(SupportSurveyTokens.IsWellFormed(token));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), survey.TokenHash);
        Assert.Equal((SupportSurveyStatus.Sent, (SupportSurveyChannel?)SupportSurveyChannel.Email), (survey.Status, survey.Channel));
    }

    // H16: send_failed y connection_unavailable se reintentan tres veces (1, 5 y 15 minutos) y después quedan Failed.
    [Fact]
    public async Task ASendFailureRetriesThreeTimesThenFails()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread();
        _channels.OnSurvey = _ => new SupportChannelSendResult.Rejected("connection_unavailable");
        var ct = TestContext.Current.CancellationToken;

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), ct);
        Assert.Equal((SupportSurveyStatus.Pending, 1, Now.AddMinutes(1)), (survey.Status, survey.Attempts, survey.NextAttemptAt));
        _channels.OnSurvey = _ => new SupportChannelSendResult.Failed(Guid.CreateVersion7(), "send_failed");
        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), ct);
        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), ct);
        Assert.Equal((SupportSurveyStatus.Pending, 3, Now.AddMinutes(15)), (survey.Status, survey.Attempts, survey.NextAttemptAt));
        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), ct);

        Assert.Equal((SupportSurveyStatus.Failed, 4, "send_failed"), (survey.Status, survey.Attempts, survey.ReasonCode));
        Assert.Equal(4, _channels.SurveySends.Count);
        Assert.All(_channels.SurveySends, send => Assert.Equal(survey.Id, send.ClientId));
    }

    // H17: sin nadie activo que firme, no sale nada: «sin encuesta», no una falla.
    [Fact]
    public async Task NoAuthorSkips()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread();
        _members.Inactive(Creator);

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Skipped, "no_author"), (survey.Status, survey.ReasonCode));
        Assert.Empty(_channels.SurveySends);
    }

    // Un contacto bloqueado (o cualquier otro rechazo del canal que no sea de envío) es «sin encuesta» con el motivo.
    [Fact]
    public async Task ABlockedContactIsWithoutSurvey()
    {
        var ticket = FromConversation("WhatsApp");
        var survey = Resolve(ticket);
        WhatsAppThread();
        _channels.OnSurvey = _ => new SupportChannelSendResult.Rejected("contact_blocked");

        await Sender().DecideAsync(survey, ticket, Settings(surveyEnabled: true), TestContext.Current.CancellationToken);

        Assert.Equal((SupportSurveyStatus.Skipped, "contact_blocked", 0), (survey.Status, survey.ReasonCode, survey.Attempts));
    }

    private sealed class FakeChannels : ISupportReplyChannels
    {
        public Dictionary<Guid, SupportThreadStatus> Threads { get; } = [];

        public HashSet<Guid> ApprovedSurveyConnections { get; } = [];

        public List<SupportChannelSurveyTemplateSend> SurveySends { get; } = [];

        public List<Guid> ThreadLookups { get; } = [];

        public Func<SupportChannelSurveyTemplateSend, SupportChannelSendResult> OnSurvey { get; set; } =
            send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);

        public Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            ThreadLookups.Add(conversationId);
            return Task.FromResult(Threads.TryGetValue(conversationId, out var thread) ? thread : null);
        }

        public Task<bool> IsSurveyTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovedSurveyConnections.Contains(connectionId));

        public Task<SupportChannelSendResult> SendSurveyTemplateAsync(
            SupportChannelSurveyTemplateSend send, CancellationToken cancellationToken)
        {
            SurveySends.Add(send);
            return Task.FromResult(OnSurvey(send));
        }

        public Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendThreadAsync(SupportChannelThreadSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The survey never goes as free text (H16).");

        public Task<SupportChannelSendResult> SendTemplateAsync(SupportChannelTemplateSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SupportConnectionOption>> ListFormalTemplateConnectionsAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsFormalTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendThreadDocumentAsync(
            SupportChannelThreadDocumentSend send, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendFormalTemplateAsync(
            SupportChannelFormalTemplateSend send, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSurveyEmail : ISupportSurveyEmail
    {
        public List<SupportSurveyEmail> Sent { get; } = [];

        public Task<SupportChannelSendResult> SendAsync(SupportSurveyEmail email, CancellationToken cancellationToken)
        {
            Sent.Add(email);
            return Task.FromResult<SupportChannelSendResult>(new SupportChannelSendResult.Sent(null, null));
        }
    }

    private sealed class FakeMembers : ISupportMemberDirectory
    {
        private readonly Dictionary<Guid, bool> _known = [];

        public void Active(Guid memberId) => _known[memberId] = true;

        public void Inactive(Guid memberId) => _known[memberId] = false;

        public Task<IReadOnlyList<SupportMemberRef>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportMemberRef>>(memberIds
                .Where(_known.ContainsKey)
                .Select(id => new SupportMemberRef(id, $"Miembro {id:N}", _known[id], _known[id]))
                .ToList());

        public Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SupportMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportMemberRef>>([]);

        public Task<IReadOnlyList<SupportMemberRef>> ListTaskMembersAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportMemberRef>>([]);
    }

    // Sin fila de módulos el tenant los tiene todos (SupportConversationRules.IsEnabledAsync).
    private sealed class NullModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FakePublicOptions : ISupportPublicOptions
    {
        public string BaseUrl => "https://soporte.example.com";

        public string Hostname => "soporte.example.com";
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeTenants : ITenantDirectory
    {
        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("tienda");

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("Tienda de Ana");

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("America/Bogota");

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("COP");

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantStatus?>(TenantStatus.Active);

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantRegionalSettings?>(
                new TenantRegionalSettings("America/Bogota", TenantDateFormats.DayMonthYear, "COP", TenantNumberFormats.CommaDecimal));
    }
}
