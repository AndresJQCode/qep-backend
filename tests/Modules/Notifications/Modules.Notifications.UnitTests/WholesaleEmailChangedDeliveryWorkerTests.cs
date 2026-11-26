using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Messaging;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Spec 2026-11-19 §6: al aceptar con otro correo, el distribuidor queda con el de la cuenta y se avisa a las dos direcciones.
/// Cada dirección es un consumidor propio del mismo evento, con su inbox: un fallo en una no frena la otra, y la aceptación ya se
/// guardó (el evento va por outbox).
/// </summary>
public sealed class WholesaleEmailChangedDeliveryWorkerTests
{
    private const string EventName = "wholesale.distributor-email-changed.v1";
    private const string Previous = "ana@distri.co";
    private const string New = "compras@norte.co";
    private const string Wholesaler = "Mayorista Central";
    private const string Business = "Distribuciones Norte SAS";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Distributor = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeEmailChannel _channel = new();
    private readonly FakeTenantNames _tenants = new() { Names = { [Tenant] = Wholesaler } };

    [Fact]
    public async Task ItSendsOneEmailToThePreviousAndOneToTheNewAddress()
    {
        var toPrevious = await DeliverAsync(new WholesaleEmailChangedToPreviousDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToPreviousDeliveryWorker>.Instance));
        var toNew = await DeliverAsync(new WholesaleEmailChangedToNewDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToNewDeliveryWorker>.Instance));

        Assert.Equal([Previous, New], _channel.Sent.Select(message => message.ToAddress));
        Assert.Equal((NotificationStatus.Sent, Previous, (Guid?)Tenant, Distributor),
            (toPrevious.Status, toPrevious.RecipientAddress, toPrevious.TenantId, toPrevious.RecipientId));
        Assert.Equal((NotificationStatus.Sent, New), (toNew.Status, toNew.RecipientAddress));
        Assert.Equal(WholesaleEmailChangedEmailTemplate.TemplateRef, toPrevious.TemplateRef);
        foreach (var message in _channel.Sent)
        {
            Assert.Equal($"Cambió el correo de {Business} con {Wholesaler}", message.Subject);
            foreach (var body in new[] { message.HtmlBody, message.TextBody })
            {
                Assert.Contains(Previous, body, StringComparison.Ordinal);
                Assert.Contains(New, body, StringComparison.Ordinal);
                Assert.Contains($"Si no reconoces este cambio, escríbele a {Wholesaler}.", body, StringComparison.Ordinal);
            }
        }

        Assert.Contains("Desde ahora el correo de contacto es " + New + ".", _channel.Sent[0].TextBody, StringComparison.Ordinal);
        Assert.Contains("Aceptaste la invitación de " + Wholesaler, _channel.Sent[1].TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureSendingToOneStillSendsTheOther()
    {
        _channel.FailFor.Add(Previous);

        var toPrevious = await DeliverAsync(new WholesaleEmailChangedToPreviousDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToPreviousDeliveryWorker>.Instance));
        var toNew = await DeliverAsync(new WholesaleEmailChangedToNewDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToNewDeliveryWorker>.Instance));

        Assert.Equal(NotificationStatus.Failed, toPrevious.Status);
        Assert.Equal(NotificationStatus.Sent, toNew.Status);
        Assert.Equal(New, Assert.Single(_channel.Sent).ToAddress);
    }

    [Fact]
    public async Task NoEmailContainsAToken()
    {
        // Aunque un payload trajera un token (no lo trae), el correo no lo usa: sin link ni token.
        const string Token = "Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q";
        var record = Record(token: Token);

        await new WholesaleEmailChangedToPreviousDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToPreviousDeliveryWorker>.Instance)
            .DeliverEmailAsync(record, Context(), Ct);
        await new WholesaleEmailChangedToNewDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToNewDeliveryWorker>.Instance)
            .DeliverEmailAsync(record, Context(), Ct);

        Assert.Equal(2, _channel.Sent.Count);
        foreach (var message in _channel.Sent)
        {
            var all = message.Subject + message.HtmlBody + message.TextBody;
            Assert.DoesNotContain(Token, all, StringComparison.Ordinal);
            Assert.DoesNotContain("wholesale-invitations", all, StringComparison.Ordinal);
            Assert.DoesNotContain("http", all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AMissingAddressOrWholesalerFailsWithoutSending()
    {
        var noAddress = await new WholesaleEmailChangedToPreviousDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToPreviousDeliveryWorker>.Instance)
            .DeliverEmailAsync(Record(previous: null), Context(), Ct);
        _tenants.Names.Clear();
        var noWholesaler = await new WholesaleEmailChangedToNewDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToNewDeliveryWorker>.Instance)
            .DeliverEmailAsync(Record(), Context(), Ct);

        Assert.Equal((NotificationStatus.Failed, "recipient_email_unavailable"), (noAddress.Status, noAddress.FailureReason));
        Assert.Equal((NotificationStatus.Failed, "tenant_name_unavailable"), (noWholesaler.Status, noWholesaler.FailureReason));
        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public void BothConsumersReadTheSameEventWithTheirOwnInbox()
    {
        var toPrevious = new WholesaleEmailChangedToPreviousDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToPreviousDeliveryWorker>.Instance);
        var toNew = new WholesaleEmailChangedToNewDeliveryWorker(null!, NullLogger<WholesaleEmailChangedToNewDeliveryWorker>.Instance);

        Assert.Equal(EventName, WholesaleEmailChangedDeliveryWorker.Event);
        Assert.NotEqual(toPrevious.ConsumerName, toNew.ConsumerName);
    }

    private Task<Notification> DeliverAsync(WholesaleEmailChangedDeliveryWorker worker) =>
        worker.DeliverEmailAsync(Record(), Context(), Ct);

    private DeliveryContext Context() =>
        new(_channel, null!, _tenants, null!, new FixedClock(Now), null!, null!);

    private static OutboxRecord Record(string? previous = Previous, string? token = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tenantId"] = Tenant,
            ["distributorId"] = Distributor,
            ["previousEmail"] = previous,
            ["newEmail"] = New,
            ["businessName"] = Business,
            ["changedAt"] = Now,
        };
        if (token is not null)
        {
            payload["token"] = token;
        }

        return new OutboxRecord
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredAt = Now,
        };
    }

    private sealed class FakeEmailChannel : IEmailChannel
    {
        public List<EmailMessage> Sent { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(message.ToAddress))
            {
                throw new InvalidOperationException("Infobip is down.");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeTenantNames : ITenantDirectory
    {
        public Dictionary<Guid, string> Names { get; } = [];

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Names.TryGetValue(tenantId.Value, out var name) ? name : null);

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
