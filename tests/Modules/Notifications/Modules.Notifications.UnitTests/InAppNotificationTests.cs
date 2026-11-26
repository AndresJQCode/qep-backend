using System.Text.Json;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>Spec 2026-11-22 §11: an entry of a member's bell. It stores ids and a kind, never rendered text.</summary>
public sealed class InAppNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    [Fact]
    public void CreateKeepsTheMemberAndKind()
    {
        var resource = Guid.CreateVersion7();
        var message = Guid.CreateVersion7();

        var entry = InAppNotification.Create(
            Tenant, Member, InAppNotificationKind.SupportCustomerReplied, resource, """{"ticketId":"x"}""", "key-1", message, Now);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(Tenant, entry.TenantId);
        Assert.Equal(Member, entry.MemberId);
        Assert.Equal(InAppNotificationKind.SupportCustomerReplied, entry.Kind);
        Assert.Equal(resource, entry.ResourceId);
        Assert.Equal("key-1", entry.DedupeKey);
        Assert.Equal(message, entry.SourceMessageId);
        Assert.Equal(Now, entry.CreatedAt);
        Assert.Null(entry.ReadAt);
        Assert.Null(entry.ResolvedAt);
    }

    [Fact]
    public void MarkReadIsIdempotentAndKeepsTheFirstReadAt()
    {
        var entry = NewEntry();

        Assert.True(entry.MarkRead(Now));
        Assert.False(entry.MarkRead(Now.AddHours(1)));

        Assert.Equal(Now, entry.ReadAt);
    }

    [Fact]
    public void ResolveMarksReadAndSetsTheResolution()
    {
        var entry = NewEntry("""{"orderId":"o","resolution":null}""");

        var changed = entry.Resolve("Approved", Now);

        Assert.True(changed);
        Assert.Equal(Now, entry.ResolvedAt);
        Assert.Equal(Now, entry.ReadAt);
        using var data = JsonDocument.Parse(entry.DataJson);
        Assert.Equal("Approved", data.RootElement.GetProperty("resolution").GetString());
        Assert.Equal("o", data.RootElement.GetProperty("orderId").GetString());
    }

    [Fact]
    public void ResolvingTwiceKeepsTheFirstResolution()
    {
        var entry = NewEntry("""{"orderId":"o","resolution":null}""");
        entry.Resolve("Approved", Now);

        var again = entry.Resolve("Cancelled", Now.AddHours(1));

        Assert.False(again);
        Assert.Equal(Now, entry.ResolvedAt);
        using var data = JsonDocument.Parse(entry.DataJson);
        Assert.Equal("Approved", data.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public void ToStringDoesNotPrintTheData()
    {
        var entry = NewEntry("""{"fileName":"secreto.xlsx"}""");

        var text = entry.ToString();

        Assert.DoesNotContain("secreto", text, StringComparison.Ordinal);
        Assert.Contains(nameof(InAppNotificationKind.ExportReady), text, StringComparison.Ordinal);
    }

    private static InAppNotification NewEntry(string data = "{}") =>
        InAppNotification.Create(Tenant, Member, InAppNotificationKind.ExportReady, null, data, "key", null, Now);
}
