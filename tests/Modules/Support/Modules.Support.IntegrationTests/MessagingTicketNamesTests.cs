using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (plan E11): el adaptador de Bootstrapper que le da a Mensajería el número de los tickets que nombran los eventos
/// <c>TicketOpened</c>/<c>TicketClosed</c> del hilo. Lee <c>support.tickets</c> por el esquema migrado, sólo del tenant que pregunta;
/// un id que no está no aparece y el hilo muestra el evento sin número.
/// </summary>
public sealed class MessagingTicketNamesTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReturnsTheFormattedNumberOfTheTenantTicketsOnly()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = Guid.Parse(NewTenantId());
        var other = Guid.Parse(NewTenantId());
        await SeedTenantAsync(factory, tenant.ToString());
        await SeedTenantAsync(factory, other.ToString());
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var ticketId = await SaveTicketAsync(factory, tenant, 127, actor);
        var unknown = Guid.CreateVersion7();

        await using var scope = factory.Services.CreateAsyncScope();
        var names = scope.ServiceProvider.GetRequiredService<IMessagingTicketNames>();

        var mine = await names.FindNumbersAsync(tenant, [ticketId.Value, unknown], Ct);
        var theirs = await names.FindNumbersAsync(other, [ticketId.Value], Ct);
        var none = await names.FindNumbersAsync(tenant, [], Ct);

        Assert.Equal("SOP-000127", Assert.Single(mine, pair => pair.Key == ticketId.Value).Value);
        Assert.False(mine.ContainsKey(unknown));
        Assert.Empty(theirs);
        Assert.Empty(none);
    }

    private static async Task<SupportTicketId> SaveTicketAsync(QepApiFactory factory, Guid tenant, long number, Guid actor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var type = (await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct))[0];
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, number, type, "Asunto", "Descripción", ManualIntakeMedium.Phone, SupportReplyChannel.Email, null,
            new TicketRequester("Ana", "Gómez", null, null, null, "ana@example.com", null, null, "CO", null),
            null, actor, Now, Clock);
        scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return ticket.Id;
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
