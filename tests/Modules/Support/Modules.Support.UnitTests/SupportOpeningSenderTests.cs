using System.Globalization;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// Tarea 8 (plan F10, F11, F24 a F26 y F29): la información del ticket con el PIN por su canal. Un ticket de una conversación la recibe
/// como texto por el hilo, sólo con la ventana de 24 h abierta y firmada por un miembro activo (quien lo creó, su responsable o un
/// gestor). Con dobles de los puertos: lo que hace Mensajería con el envío ya lo amarran sus pruebas.
/// </summary>
public sealed class SupportOpeningSenderTests
{
    private const string Pin = "123456";
    private const string ConsultUrl = "https://soporte.example.com/tienda/consultar?radicado=SOP-000001";

    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Creator = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly Guid ConversationId = Guid.Parse("0193a000-0000-7000-8000-0000000000d1");
    private static readonly Guid FormVersion = Guid.Parse("0193a000-0000-7000-8000-0000000000f1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, null, null, "ana@example.com", "+573001234567", null, "CO", null);

    private readonly FakeChannels _channels = new();
    private readonly FakeMembers _members = new();
    private readonly FakeOpeningEmail _email = new();
    private readonly FakeTickets _tickets = new();
    private readonly FakeTenants _tenants = new();

    private SupportOpeningSender Sender => new(_email, _channels, _members, _tickets, _tenants);

    [Fact]
    public async Task InsideTheWindowItSendsTextWithTheNumberAndPin()
    {
        var ticket = FromConversation();
        OpenWindow();
        _members.Active(Creator);

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(new SupportOpeningOutcome(SupportOpeningSender.ThreadChannel, null, true), outcome);
        var send = Assert.Single(_channels.ThreadSends);
        Assert.Equal(TenantId, send.TenantId);
        Assert.Equal(ConversationId, send.ConversationId);
        Assert.Equal(Creator, send.AuthorMemberId);
        Assert.Null(send.UploadId);
        Assert.Contains(ticket.DisplayNumber, send.Text, StringComparison.Ordinal);
        Assert.Contains(Pin, send.Text, StringComparison.Ordinal);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task OutsideTheWindowItSendsNothingAndRecordsWindowClosed()
    {
        var ticket = FromConversation();
        _members.Active(Creator);
        _channels.Threads[ConversationId] = Thread(windowClosesAt: Received.AddMinutes(-1));

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(
            new SupportOpeningOutcome(SupportOpeningSender.ThreadChannel, SupportChannelReasons.WindowClosed, false), outcome);
        Assert.Empty(_channels.ThreadSends);
    }

    [Fact]
    public async Task AContactThatNeverWroteHasNoWindow()
    {
        var ticket = FromConversation();
        _members.Active(Creator);
        _channels.Threads[ConversationId] = Thread(windowClosesAt: null);

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(SupportChannelReasons.WindowClosed, outcome.FailureCode);
        Assert.Empty(_channels.ThreadSends);
    }

    [Fact]
    public async Task TheAuthorIsTheCreatorThenTheAssigneeThenAManager()
    {
        OpenWindow();

        // Quien lo creó, si sigue activo.
        _members.Active(Creator);
        _members.Active(Assignee);
        await Sender.SendAsync(FromConversation(assignee: Assignee), Pin, Received, ConsultUrl, CancellationToken.None);

        // Sin él, el responsable.
        _members.Inactive(Creator);
        await Sender.SendAsync(FromConversation(assignee: Assignee), Pin, Received, ConsultUrl, CancellationToken.None);

        // Sin ninguno de los dos, el primer gestor por nombre (el orden de GET /assignees).
        _members.Inactive(Assignee);
        var zoe = Guid.Parse("0193a000-0000-7000-8000-0000000000b2");
        var beto = Guid.Parse("0193a000-0000-7000-8000-0000000000b1");
        _members.Managers.Add(new SupportMemberRef(zoe, "Zoe", true, true));
        _members.Managers.Add(new SupportMemberRef(beto, "beto", true, true));
        await Sender.SendAsync(FromConversation(assignee: Assignee), Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal([Creator, Assignee, beto], _channels.ThreadSends.Select(send => send.AuthorMemberId));
    }

    [Fact]
    public async Task WithoutAnyAuthorItRecordsNoAuthor()
    {
        var ticket = FromConversation(assignee: Assignee);
        OpenWindow();
        _members.Inactive(Creator);
        _members.Inactive(Assignee);

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(new SupportOpeningOutcome(SupportOpeningSender.ThreadChannel, SupportChannelReasons.NoAuthor, false), outcome);
        Assert.Empty(_channels.ThreadSends);
    }

    [Fact]
    public async Task AnAnonymousTicketSendsNothing()
    {
        var ticket = SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), TenantId, 3, Type(0), "Denuncia", "Sin datos.", null, null, FormVersion, Received, Clock);

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.False(SupportOpeningSender.HasDestination(ticket));
        Assert.Equal(new SupportOpeningOutcome(SupportOpeningSender.NoChannel, null, false), outcome);
        Assert.Empty(_channels.ThreadSends);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task TheTextTuteaAndCarriesTheLinkWithoutThePin()
    {
        var ticket = FromConversation();
        OpenWindow();
        _members.Active(Creator);
        _tenants.DateFormat = TenantDateFormats.DayMonthYear;

        await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        var due = ticket.DueOn!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        Assert.Equal(
            $"Registramos tu caso {ticket.DisplayNumber} (Petición general). Fecha límite de respuesta: {due}. " +
            $"Consulta su estado en {ConsultUrl} con la clave {Pin}.",
            Assert.Single(_channels.ThreadSends).Text);
        Assert.DoesNotContain(Pin, ConsultUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypeWithoutTermSaysThereIsNoDeadlineAndTheDateFollowsTheTenantFormat()
    {
        OpenWindow();
        _members.Active(Creator);
        _tenants.DateFormat = TenantDateFormats.IsoDate;

        await Sender.SendAsync(FromConversation(typeIndex: 7), Pin, Received, ConsultUrl, CancellationToken.None);
        var withTerm = FromConversation();
        await Sender.SendAsync(withTerm, Pin, Received, ConsultUrl, CancellationToken.None);

        var texts = _channels.ThreadSends.Select(send => send.Text).ToList();
        Assert.Contains("(Soporte técnico). Fecha límite de respuesta: sin fecha límite.", texts[0], StringComparison.Ordinal);
        Assert.Contains(
            $"Fecha límite de respuesta: {withTerm.DueOn!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
            texts[1],
            StringComparison.Ordinal);
    }

    // Un reintento de la misma emisión del PIN no duplica (Mensajería es idempotente por ClientId); un PIN nuevo es otro mensaje.
    [Fact]
    public async Task TheClientIdIsTheSameForTheSameIssuanceAndChangesWithANewOne()
    {
        var ticket = FromConversation();
        OpenWindow();
        _members.Active(Creator);

        await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);
        await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);
        await Sender.SendAsync(ticket, "654321", Received.AddMinutes(11), ConsultUrl, CancellationToken.None);

        var ids = _channels.ThreadSends.Select(send => send.ClientId).ToList();
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
        Assert.NotEqual(Guid.Empty, ids[0]);
    }

    // Triage #20 (Bloque F): pin_issued_at se guarda en microsegundos (timestamptz). El instante en memoria y el que vuelve de la base dan
    // el mismo ClientId, así un reintento armado desde la fila no manda un segundo mensaje.
    [Fact]
    public void TheClientIdIgnoresTheSubMicrosecondTicksThatTheDatabaseDrops()
    {
        var id = SupportTicketId.New();
        var inMemory = Received.AddTicks(7);
        var fromDatabase = Received;

        Assert.Equal(SupportOpeningSender.ClientIdFor(id, fromDatabase), SupportOpeningSender.ClientIdFor(id, inMemory));
        Assert.NotEqual(SupportOpeningSender.ClientIdFor(id, fromDatabase), SupportOpeningSender.ClientIdFor(id, Received.AddTicks(10)));
    }

    [Fact]
    public async Task AFailedThreadSendIsRecordedWithItsCode()
    {
        var ticket = FromConversation();
        OpenWindow();
        _members.Active(Creator);
        _channels.OnThread = _ => new SupportChannelSendResult.Failed(Guid.CreateVersion7(), SupportChannelReasons.SendFailed);

        var outcome = await Sender.SendAsync(ticket, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(new SupportOpeningOutcome(SupportOpeningSender.ThreadChannel, SupportChannelReasons.SendFailed, false), outcome);
    }

    // Plan F26: el manual por WhatsApp sin conversación no tiene a dónde; el manual por correo va al correo.
    [Fact]
    public async Task AManualTicketGoesByEmailOnlyWithTheEmailChannel()
    {
        var byWhatsApp = Manual(SupportReplyChannel.WhatsAppTemplate);
        var byThread = Manual(SupportReplyChannel.Thread);
        var byEmail = Manual(SupportReplyChannel.Email);

        Assert.False(SupportOpeningSender.HasDestination(byWhatsApp));
        Assert.False(SupportOpeningSender.HasDestination(byThread));
        Assert.Equal(SupportOpeningSender.EmailChannel, SupportOpeningSender.ChannelOf(byEmail));
        Assert.Equal(SupportOpeningSender.ThreadChannel, SupportOpeningSender.ChannelOf(FromConversation()));

        var outcome = await Sender.SendAsync(byEmail, Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.True(outcome.Sent);
        var email = Assert.Single(_email.Sent);
        Assert.Equal("ana@example.com", email.ToAddress);
        Assert.Equal(Pin, email.Pin);
        Assert.Empty(_channels.ThreadSends);
    }

    // Spec 2026-11-22 (i18n): quien abre el caso no tiene preferencia propia; el correo va en el idioma del tenant.
    [Theory]
    [InlineData("es-419")]
    [InlineData("es-ES")]
    public async Task TheOpeningEmailCarriesTheTenantLanguage(string language)
    {
        _tenants.Language = language;

        await Sender.SendAsync(Manual(SupportReplyChannel.Email), Pin, Received, ConsultUrl, CancellationToken.None);

        Assert.Equal(language, Assert.Single(_email.Sent).Language);
    }

    private static SupportTicketType Type(int index) => SupportTicketTypeDefaults.For(TenantId, Received)[index];

    private static SupportTicket FromConversation(Guid? assignee = null, int typeIndex = 0) =>
        SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 1, Type(typeIndex), "No me llegó el pedido", "Lo escribió por WhatsApp.",
            SupportTicketPriority.Medium,
            new SupportConversationRef(ConversationId, "WhatsApp"), null, Person, assignee, Creator, Received, Clock);

    private static SupportTicket Manual(SupportReplyChannel channel) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 2, Type(0), "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, channel, null, Person,
            null, Creator, Received, Clock);

    private static SupportThreadStatus Thread(DateTimeOffset? windowClosesAt) =>
        new(ConversationId, "WhatsApp", false, windowClosesAt, Guid.Parse("0193a000-0000-7000-8000-0000000000e1"));

    private void OpenWindow() => _channels.Threads[ConversationId] = Thread(windowClosesAt: Received.AddHours(5));

    private sealed class FakeChannels : ISupportReplyChannels
    {
        public Dictionary<Guid, SupportThreadStatus> Threads { get; } = [];

        public List<SupportChannelThreadSend> ThreadSends { get; } = [];

        public Func<SupportChannelThreadSend, SupportChannelSendResult> OnThread { get; set; } =
            send => new SupportChannelSendResult.Sent(Guid.CreateVersion7(), send.ConversationId);

        public Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(Threads.TryGetValue(conversationId, out var thread) ? thread : null);

        public Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendThreadAsync(SupportChannelThreadSend send, CancellationToken cancellationToken)
        {
            ThreadSends.Add(send);
            return Task.FromResult(OnThread(send));
        }

        public Task<SupportChannelSendResult> SendTemplateAsync(SupportChannelTemplateSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The opening never uses the template (plan F24).");

        public Task<IReadOnlyList<SupportConnectionOption>> ListFormalTemplateConnectionsAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsFormalTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendThreadDocumentAsync(
            SupportChannelThreadDocumentSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The opening never sends a document.");

        public Task<SupportChannelSendResult> SendFormalTemplateAsync(
            SupportChannelFormalTemplateSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The opening never sends a document.");

        public Task<bool> IsSurveyTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportChannelSendResult> SendSurveyTemplateAsync(
            SupportChannelSurveyTemplateSend send, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The opening never sends the survey.");
    }

    private sealed class FakeMembers : ISupportMemberDirectory
    {
        private readonly Dictionary<Guid, bool> _known = [];

        public List<SupportMemberRef> Managers { get; } = [];

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
            Task.FromResult<IReadOnlyList<SupportMemberRef>>(Managers.ToList());

        public Task<IReadOnlyList<SupportMemberRef>> ListTaskMembersAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportMemberRef>>(Managers.ToList());
    }

    private sealed class FakeOpeningEmail : ISupportOpeningEmail
    {
        public List<SupportOpeningEmail> Sent { get; } = [];

        public Task<SupportChannelSendResult> SendAsync(SupportOpeningEmail email, CancellationToken cancellationToken)
        {
            Sent.Add(email);
            return Task.FromResult<SupportChannelSendResult>(new SupportChannelSendResult.Sent(null, null));
        }
    }

    // Los tickets de estas pruebas traen su evento Created en memoria: la traza guardada no se lee.
    private sealed class FakeTickets : ISupportTicketRepository
    {
        public Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(
            Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A ticket created in memory carries its Created event.");

        public Task<IReadOnlyList<SupportTicketSurvey>> ListSurveysAsync(
            Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SupportTicketSurvey?> FindLatestSurveyAsync(
            Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SupportTicketSurvey?> FindSurveyByTokenHashAsync(
            Guid tenantId, byte[] tokenHash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportInboxTotals> TotalsAsync(
            Guid tenantId, SupportTicketKind kind, Guid? participantMemberId, DateOnly today, DateOnly? dueSoonLimit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SupportTicket>> ListOpenByConversationAsync(
            Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SupportTicket>> ListByConversationOrCustomerAsync(
            Guid tenantId, Guid conversationId, Guid? customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportTicket?> FindByClientRequestAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyOfTypeAsync(Guid tenantId, SupportTicketTypeId typeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<SupportTicketTypeId>> UsedTypeIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SupportTicket ticket) => throw new NotSupportedException();
    }

    private sealed class FakeTenants : ITenantDirectory
    {
        public string DateFormat { get; set; } = TenantDateFormats.DayMonthYear;

        public string Language { get; set; } = "es-419";

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
                new TenantRegionalSettings(
                    "America/Bogota", DateFormat, "COP", TenantNumberFormats.CommaDecimal, Language: Language));
    }
}
