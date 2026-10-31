using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

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

    public List<(ClaimedRecipient Recipient, DateTimeOffset NextAttemptAt)> Deferred { get; } = [];

    public Task DeferAsync(ClaimedRecipient recipient, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        Order.Add("defer");
        Deferred.Add((recipient, nextAttemptAt));
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

/// <summary>Un reloj que sólo avanza cuando alguien lo mueve.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

    public void Advance(TimeSpan span) => _ticks += span.Ticks;

    /// <summary>El «Task.Delay» de las pruebas: no duerme, avanza el reloj.</summary>
    public Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance(span);
        return Task.CompletedTask;
    }
}

/// <summary>Decisión del owner 2026-10-12 (P3 y P5): el presupuesto de envío compartido, en memoria. Por defecto concede todo lo pedido;
/// <see cref="Grant"/> decide cuántos concede por llamada y <see cref="Acquired"/> / <see cref="Throttled"/> anotan lo que el despachador pidió.</summary>
internal sealed class FakeSendBudget : IConnectionSendBudget
{
    public Func<Guid, int, SendGrant> Grant { get; set; } = (_, requested) => new SendGrant(requested, TimeSpan.Zero, 1000);

    public List<(Guid ConnectionId, int Requested, DateTimeOffset Now)> Acquired { get; } = [];

    public List<(Guid ConnectionId, DateTimeOffset Now)> Throttled { get; } = [];

    public Task<SendGrant> AcquireAsync(Guid tenantId, Guid connectionId, int requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Acquired.Add((connectionId, requested, now));
        return Task.FromResult(Grant(connectionId, requested));
    }

    public Task ThrottleAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Throttled.Add((connectionId, now));
        return Task.CompletedTask;
    }
}

/// <summary>El huso del tenant, de mentira: Bogotá (UTC-5, sin horario de verano), para que la hora local nunca coincida con la del reloj en
/// UTC. <see cref="Calls"/> cuenta las lecturas, porque una campaña sin saludo no tiene por qué ir a buscarlo.</summary>
internal sealed class FakeTenantClock(IClock clock) : ITenantClock
{
    public int Calls { get; private set; }

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new TenantCalendar(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }
}

/// <summary>El despachador sobre el banco de Messaging: el mismo tenant, conexión Active (111/token), catálogo con <c>promo</c>, clientes
/// y reloj fijo, más un presupuesto de envío de mentira.</summary>
internal sealed class DispatchBed
{
    public DispatchBed()
    {
        Bed = new MessagingTestBed();
        Bed.Templates.Catalog.Add(FakeMessageTemplates.Hello("promo", "es", "MARKETING"));
        Clock = new FakeClock(Bed.Now);
        TenantClock = new FakeTenantClock(Clock);
        Dispatcher = new CampaignDispatcher(Store, Bed.Meta, Bed.Connections, Bed.Customers, Bed.Templates, Budget, Clock, TenantClock);
    }

    public MessagingTestBed Bed { get; }

    public FakeDispatchStore Store { get; } = new();

    public FakeSendBudget Budget { get; } = new();

    public FakeClock Clock { get; }

    public FakeTenantClock TenantClock { get; }

    public CampaignDispatcher Dispatcher { get; }

    public Guid CampaignId { get; } = Guid.CreateVersion7();

    public ClaimedRecipient Recipient(
        int attempts = 1, string name = "Laura Gómez", string? waId = "573001234567", string? userId = "CO.573001234567", Guid? messageId = null,
        string category = "MARKETING", IReadOnlyDictionary<string, CampaignParameter>? parameters = null,
        bool active = true, bool optOut = false, bool inDirectory = true)
    {
        // R6: antes de mandar una de marketing el worker vuelve a mirar la ficha, así que el cliente tiene que existir en el directorio.
        var customerId = Guid.CreateVersion7();
        if (inDirectory)
        {
            Bed.Customers.Contacts[customerId] = new MessagingCustomerContact(customerId, name, waId is null ? null : "+" + waId, userId, active, optOut);
        }

        return new ClaimedRecipient(
            Guid.CreateVersion7(), CampaignId, Bed.TenantId, customerId, name, waId, userId, attempts, messageId, ConversationId: null, Bed.ConnectionId,
            "promo", "es", category,
            parameters ?? new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.CustomerName, ["2"] = CampaignParameter.Text("A1") },
            Bed.MemberId);
    }

    public static MessagingCustomerContact Contact(
        string name = "Laura", string? phone = "+573001234567", string? userId = null, bool active = true, bool optOut = false) =>
        new(Guid.CreateVersion7(), name, phone, userId, active, optOut);
}
