using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Persistence;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Chatbot CRM action spec (2026-11-19): what the chatbot does to a customer is audited as the system with source
/// <c>chatbot</c>, so the log tells it apart from the data-consent reply and from a person. The default stays
/// <c>customers</c>. The outbox row is inspected before saving: nothing touches a database.
/// </summary>
public sealed class CustomersAuditSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static CustomersDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .Options);

    private static JsonElement SinglePayload(CustomersDbContext context) =>
        JsonDocument.Parse(context.Outbox.Local.Single().PayloadJson).RootElement;

    [Fact]
    public void PublishBySystemWithSourceStoresIt()
    {
        using var context = NewContext();
        var publisher = new CustomersAuditPublisher(context);

        publisher.PublishBySystem(
            Guid.CreateVersion7(), CustomerAuditActions.TagsChanged, Guid.CreateVersion7().ToString(), "success", Now,
            source: CustomerAuditSources.Chatbot);

        var payload = SinglePayload(context);
        Assert.Equal("chatbot", payload.GetProperty("source").GetString());
        Assert.Equal("System", payload.GetProperty("actorType").GetString());
        Assert.Equal(Guid.Empty, payload.GetProperty("actorId").GetGuid());
    }

    [Fact]
    public void PublishBySystemWithoutSourceStaysCustomers()
    {
        using var context = NewContext();
        var publisher = new CustomersAuditPublisher(context);

        publisher.PublishBySystem(
            Guid.CreateVersion7(), CustomerAuditActions.DataConsentAccepted, Guid.CreateVersion7().ToString(), "success", Now);

        Assert.Equal("customers", SinglePayload(context).GetProperty("source").GetString());
    }
}
