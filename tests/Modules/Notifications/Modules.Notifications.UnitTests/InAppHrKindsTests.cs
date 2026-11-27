using System.Text.Json;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>Spec 2026-11-23 §9.3 and 2026-11-22 §8.5: the two medical exam kinds always carry every key of the kind, <c>null</c> when absent.</summary>
public sealed class InAppHrKindsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static async Task<JsonElement> DataOfAsync(InAppNotificationKind kind, string storedData)
    {
        var entry = InAppNotification.Create(Tenant, Guid.CreateVersion7(), kind, null, storedData, "k", null, DateTimeOffset.UtcNow);
        var dtos = await InAppNotificationMapper.ToDtosAsync(
            [entry], Tenant, new NoNames(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(kind.ToString(), dtos[0].Kind);
        return dtos[0].Data;
    }

    [Fact]
    public async Task MapperKeysAreAlwaysPresentNullWhenMissing()
    {
        var due = await DataOfAsync(InAppNotificationKind.HrMedicalExamsDue, "{}");
        var exit = await DataOfAsync(InAppNotificationKind.HrExitExamRequired, JsonSerializer.Serialize(new { employeeName = "Ana Pérez" }));

        Assert.Equal(["pendingCount", "runDate"], due.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(due.EnumerateObject(), property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
        Assert.Equal(
            ["dueDate", "employeeId", "employeeName"],
            exit.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Ana Pérez", exit.GetProperty("employeeName").GetString());
        Assert.Equal(JsonValueKind.Null, exit.GetProperty("employeeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, exit.GetProperty("dueDate").ValueKind);
    }

    [Fact]
    public async Task MapperServesOnlyTheKeysOfTheKind()
    {
        var employeeId = Guid.CreateVersion7();
        var exit = await DataOfAsync(
            InAppNotificationKind.HrExitExamRequired,
            JsonSerializer.Serialize(new { employeeId, employeeName = "Ana Pérez", dueDate = "2026-12-07", memberId = Guid.NewGuid(), restrictions = "secret" }));

        Assert.Equal(employeeId, exit.GetProperty("employeeId").GetGuid());
        Assert.Equal("2026-12-07", exit.GetProperty("dueDate").GetString());
        Assert.False(exit.TryGetProperty("memberId", out _));
        Assert.False(exit.TryGetProperty("restrictions", out _));
    }

    [Fact]
    public async Task MapperKeepsTheBirthdayKeys()
    {
        var stored = JsonSerializer.Serialize(new
        {
            runDate = "2026-11-26",
            today = new[] { new { employeeId = Guid.CreateVersion7(), employeeName = "Ana Pérez" } },
            todayTotal = 1,
            upcoming = Array.Empty<object>(),
            upcomingTotal = 0,
            upcomingDate = (string?)null,
            memberId = Guid.NewGuid(),
            birthDate = "1990-11-26",
        });

        var data = await DataOfAsync(InAppNotificationKind.HrBirthdays, stored);

        Assert.Equal(
            ["runDate", "today", "todayTotal", "upcoming", "upcomingDate", "upcomingTotal"],
            data.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Ana Pérez", Assert.Single(data.GetProperty("today").EnumerateArray()).GetProperty("employeeName").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("upcomingDate").ValueKind);
        Assert.False(data.TryGetProperty("birthDate", out _));
        Assert.False(data.TryGetProperty("memberId", out _));
        Assert.True(nameof(InAppNotificationKind.HrBirthdays).Length <= 64);
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
