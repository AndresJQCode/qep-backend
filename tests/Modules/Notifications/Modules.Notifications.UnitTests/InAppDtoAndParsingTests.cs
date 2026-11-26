using System.Globalization;
using System.Text.Json;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>Spec 2026-11-22 §8.1, §8.4 and §13: the parsing of client input and what the DTOs print.</summary>
public sealed class InAppDtoAndParsingTests
{
    [Fact]
    public void UpToWithAnOffsetIsNormalizedToUtc()
    {
        Assert.True(InAppUpTo.TryParse("2026-11-22T15:04:05.123+05:00", out var value));

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 4, 5, 123, TimeSpan.Zero), value);
    }

    [Fact]
    public void UpToWithoutAZoneIsRefused()
    {
        Assert.False(InAppUpTo.TryParse("2026-11-22T15:04:05", out _));
    }

    [Fact]
    public void ACursorWithAnOffsetIsNormalizedToUtc()
    {
        var id = Guid.CreateVersion7();
        var text = $"2026-11-22T15:04:05.1234567+05:00|{id:D}";
        var cursor = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(text));

        Assert.True(InAppCursor.TryParse(cursor, out var value));

        Assert.Equal(TimeSpan.Zero, value.CreatedAt.Offset);
        Assert.Equal(id, value.Id);
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-22T10:04:05.1234567Z", CultureInfo.InvariantCulture).UtcTicks,
            value.CreatedAt.UtcTicks);
    }

    [Fact]
    public void ToMicrosecondsDropsTheLastTickDigit()
    {
        var value = new DateTimeOffset(2026, 11, 22, 15, 4, 5, TimeSpan.Zero).AddTicks(1234567);

        var truncated = InAppTime.ToMicroseconds(value);

        Assert.Equal(0, truncated.UtcTicks % 10);
        Assert.Equal(value.UtcTicks - 7, truncated.UtcTicks);
    }

    [Fact]
    public void TheDtosDoNotPrintTheData()
    {
        using var data = JsonDocument.Parse("""{"downloadUrl":"https://secreto.example/x"}""");
        var dto = new InAppNotificationDto(Guid.CreateVersion7(), "ExportReady", DateTimeOffset.UtcNow, null, null, data.RootElement.Clone());
        var page = new InAppNotificationPageDto([dto], null, 1);
        var read = new InAppNotificationReadDto(dto, 0);

        Assert.DoesNotContain("secreto", dto.ToString());
        Assert.DoesNotContain("secreto", page.ToString());
        Assert.DoesNotContain("secreto", read.ToString());
        Assert.Contains("ExportReady", dto.ToString());
    }

    [Theory]
    [InlineData(InAppNotificationKind.SupportTaskAssigned)]
    [InlineData(InAppNotificationKind.SupportTicketAssigned)]
    public async Task TaskAssignedServesTicketNumberAndActorNameButNoSubject(InAppNotificationKind kind)
    {
        var tenantId = Guid.CreateVersion7();
        var ticketId = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        var entry = InAppNotification.Create(
            tenantId, Guid.CreateVersion7(), kind, ticketId,
            JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", actorMemberId = actor, subject = "secreto", resolution = (string?)null }),
            "k", null, DateTimeOffset.UtcNow);
        var names = new FakeNames(new Dictionary<Guid, string> { [actor] = "Ana Gómez" });

        var dto = (await InAppNotificationMapper.ToDtosAsync([entry], tenantId, names, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))[0];

        Assert.Equal(ticketId, dto.Data.GetProperty("ticketId").GetGuid());
        Assert.Equal("SOP-000127", dto.Data.GetProperty("ticketNumber").GetString());
        Assert.Equal("Ana Gómez", dto.Data.GetProperty("actorName").GetString());
        Assert.Equal(JsonValueKind.Null, dto.Data.GetProperty("resolution").ValueKind);
        Assert.False(dto.Data.TryGetProperty("actorMemberId", out _));
        Assert.False(dto.Data.TryGetProperty("subject", out _));
    }

    [Fact]
    public async Task TaskAssignedWithoutAKnownActorHasNullActorName()
    {
        var tenantId = Guid.CreateVersion7();
        var ticketId = Guid.CreateVersion7();
        var entry = InAppNotification.Create(
            tenantId, Guid.CreateVersion7(), InAppNotificationKind.SupportTaskAssigned, ticketId,
            JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", actorMemberId = Guid.CreateVersion7(), resolution = "Reassigned" }),
            "k", null, DateTimeOffset.UtcNow);

        var dto = (await InAppNotificationMapper.ToDtosAsync(
            [entry], tenantId, new FakeNames(new Dictionary<Guid, string>()), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))[0];

        Assert.Equal(JsonValueKind.Null, dto.Data.GetProperty("actorName").ValueKind);
        Assert.Equal("Reassigned", dto.Data.GetProperty("resolution").GetString());
    }

    private sealed class FakeNames(IReadOnlyDictionary<Guid, string> members) : IInAppNotificationNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) => Task.FromResult(members);

        public Task<IReadOnlyDictionary<Guid, string>> ConversationContactNamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
