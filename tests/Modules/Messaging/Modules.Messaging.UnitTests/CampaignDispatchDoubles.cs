using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3: la persistencia del worker en memoria. Anota en <see cref="Order"/> cada paso en el orden en que el
/// despachador lo pidió, y deja a la prueba decidir qué reclama y cómo responde el armado del mensaje.</summary>
internal sealed class FakeDispatchStore : ICampaignDispatchStore
{
    public List<string> Order { get; } = [];

    public int Activated { get; set; }

    public int Recovered { get; set; }

    public int Closed { get; set; }

    public List<CampaignFreezeTarget> Unfrozen { get; } = [];

    public List<(CampaignFreezeTarget Campaign, IReadOnlyList<CampaignRecipientSeed> Batch)> Added { get; } = [];

    public List<Guid> Frozen { get; } = [];

    public List<ClaimedRecipient> ToClaim { get; } = [];

    public (int Take, DateTimeOffset Now, TimeSpan Lease)? ClaimCall { get; private set; }

    /// <summary>Qué responde el armado del mensaje; <c>null</c> = la campaña ya no está enviando.</summary>
    public Func<ClaimedRecipient, PreparedSend?> Prepare { get; set; } = recipient => new PreparedSend(recipient.MessageId ?? Guid.CreateVersion7(), recipient.ConversationId ?? Guid.CreateVersion7());

    public List<(ClaimedRecipient Recipient, string Text, string DetailsJson)> Prepared { get; } = [];

    public List<(ClaimedRecipient Recipient, PreparedSend Prepared, string Wamid)> Sent { get; } = [];

    public List<(ClaimedRecipient Recipient, PreparedSend? Prepared, string Code, int? GraphCode)> Failed { get; } = [];

    public List<(ClaimedRecipient Recipient, DateTimeOffset NextAttemptAt)> Retries { get; } = [];

    public List<(ClaimedRecipient Recipient, string Code)> Skipped { get; } = [];

    public List<(Guid CampaignId, string Code)> Stops { get; } = [];

    public Task<int> ActivateDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("activate");
        return Task.FromResult(Activated);
    }

    public Task<IReadOnlyList<CampaignFreezeTarget>> ListUnfrozenAsync(CancellationToken cancellationToken)
    {
        Order.Add("list-unfrozen");
        return Task.FromResult<IReadOnlyList<CampaignFreezeTarget>>(Unfrozen.ToList());
    }

    public Task AddRecipientsAsync(CampaignFreezeTarget campaign, IReadOnlyList<CampaignRecipientSeed> batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("add-recipients");
        Added.Add((campaign, batch));
        return Task.CompletedTask;
    }

    public Task MarkFrozenAsync(Guid campaignId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("freeze");
        Frozen.Add(campaignId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClaimedRecipient>> ClaimAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        Order.Add("claim");
        ClaimCall = (take, now, lease);
        return Task.FromResult<IReadOnlyList<ClaimedRecipient>>(ToClaim.Take(take).ToList());
    }

    public Task<PreparedSend?> PrepareAsync(ClaimedRecipient recipient, string text, string detailsJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("prepare");
        Prepared.Add((recipient, text, detailsJson));
        return Task.FromResult(Prepare(recipient));
    }

    public Task SettleSentAsync(ClaimedRecipient recipient, PreparedSend prepared, string wamid, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("settle-sent");
        Sent.Add((recipient, prepared, wamid));
        return Task.CompletedTask;
    }

    public Task SettleFailedAsync(
        ClaimedRecipient recipient, PreparedSend? prepared, string code, int? graphCode, string? title, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("settle-failed");
        Failed.Add((recipient, prepared, code, graphCode));
        return Task.CompletedTask;
    }

    public Task ScheduleRetryAsync(ClaimedRecipient recipient, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        Order.Add("retry");
        Retries.Add((recipient, nextAttemptAt));
        return Task.CompletedTask;
    }

    public Task SkipAsync(ClaimedRecipient recipient, string code, CancellationToken cancellationToken)
    {
        Order.Add("skip");
        Skipped.Add((recipient, code));
        return Task.CompletedTask;
    }

    public Task<bool> StopCampaignAsync(Guid campaignId, Guid tenantId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("stop");
        Stops.Add((campaignId, code));
        return Task.FromResult(true);
    }

    public Task<int> RecoverExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("recover");
        return Task.FromResult(Recovered);
    }

    public Task<int> CloseFinishedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Order.Add("close");
        return Task.FromResult(Closed);
    }
}

/// <summary>El despachador sobre el banco de Messaging: el mismo tenant, conexión Active (111/token), catálogo con <c>promo</c>, clientes
/// y reloj fijo, más un cubo con reloj manual para ver el ritmo.</summary>
internal sealed class DispatchBed
{
    public DispatchBed(int messagesPerSecond = 1000)
    {
        Bed = new MessagingTestBed();
        Bed.Templates.Catalog.Add(FakeMessageTemplates.Hello("promo", "es", "MARKETING"));
        Time = new ManualTimeProvider();
        Limiter = new CampaignRateLimiter(messagesPerSecond, Time, Time.Delay);
        Clock = new FakeClock(Bed.Now);
        Dispatcher = new CampaignDispatcher(Store, Bed.Meta, Bed.Connections, Bed.Customers, Bed.Templates, Limiter, Clock);
    }

    public MessagingTestBed Bed { get; }

    public FakeDispatchStore Store { get; } = new();

    public ManualTimeProvider Time { get; }

    public CampaignRateLimiter Limiter { get; }

    public FakeClock Clock { get; }

    public CampaignDispatcher Dispatcher { get; }

    public Guid CampaignId { get; } = Guid.CreateVersion7();

    public ClaimedRecipient Recipient(
        int attempts = 1, string name = "Laura Gómez", string? waId = "573001234567", string? userId = "CO.573001234567", Guid? messageId = null,
        string category = "MARKETING", IReadOnlyDictionary<string, CampaignParameter>? parameters = null) =>
        new(
            Guid.CreateVersion7(), CampaignId, Bed.TenantId, Guid.CreateVersion7(), name, waId, userId, attempts, messageId, ConversationId: null, Bed.ConnectionId,
            "promo", "es", category,
            parameters ?? new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.CustomerName, ["2"] = CampaignParameter.Text("A1") },
            Bed.MemberId);

    public static MessagingCustomerContact Contact(
        string name = "Laura", string? phone = "+573001234567", string? userId = null, bool active = true, bool optOut = false) =>
        new(Guid.CreateVersion7(), name, phone, userId, active, optOut);
}
