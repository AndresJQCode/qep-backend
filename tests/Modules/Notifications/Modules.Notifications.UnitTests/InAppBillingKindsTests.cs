using System.Text.Json;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Spec reajuste §6.6 y spec 2026-11-22 §8.5: la campana de las revisiones de precio sirve sus tres claves por nombre. Sin ellas
/// en el mapeo la entrada llegaría a la pantalla sin datos.
/// </summary>
public sealed class InAppBillingKindsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public async Task ThePriceReviewKindServesItsThreeKeys()
    {
        var stored = JsonSerializer.Serialize(new { dueCount = 2, heldCount = 1, runDate = "2026-03-10", memberId = Guid.NewGuid() });
        var entry = InAppNotification.Create(
            Tenant, Guid.CreateVersion7(), InAppNotificationKind.ServicePriceReviewsDue, null, stored, "k", null, DateTimeOffset.UtcNow);

        var dto = Assert.Single(await InAppNotificationMapper.ToDtosAsync(
            [entry], Tenant, new NoNames(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        Assert.Equal("ServicePriceReviewsDue", dto.Kind);
        Assert.Equal(
            ["dueCount", "heldCount", "runDate"],
            dto.Data.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, dto.Data.GetProperty("dueCount").GetInt32());
        Assert.Equal(1, dto.Data.GetProperty("heldCount").GetInt32());
        Assert.Equal("2026-03-10", dto.Data.GetProperty("runDate").GetString());
    }

    private sealed class NoNames : IInAppNotificationNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyDictionary<Guid, string>> ConversationContactNamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
