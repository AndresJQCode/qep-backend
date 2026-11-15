using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Guarda tickets por el dominio y el repositorio, sin la API: lo usan las pruebas de los adaptadores y del seguimiento del hilo,
/// que necesitan un ticket con una ventana de seguimiento exacta. El radicado sale del generador real, dentro de una transacción.
/// </summary>
internal static class SupportTicketSeed
{
    public static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Un ticket abierto desde <paramref name="conversationId"/> que sigue el hilo desde <paramref name="followSince"/>.</summary>
    public static async Task<SupportTicket> SaveConversationTicketAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid conversationId,
        DateTimeOffset followSince,
        Guid? actorMemberId = null,
        string channel = SupportConversationRef.WhatsApp,
        Guid? customerId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenantId, Ct);
        var type = (await services.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenantId, Ct))[0];
        var unitOfWork = services.GetRequiredService<ISupportUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        var number = await services.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenantId, Ct);
        var ticket = SupportTicket.CreateFromConversation(
            SupportTicketId.New(), tenantId, number, type, "Pedido sin llegar", "El pedido no ha llegado.",
            new SupportConversationRef(conversationId, channel), customerId,
            new TicketRequester("Ana", "Gómez", null, null, null, null, "+573001234567", null, "CO", null),
            null, actorMemberId ?? Guid.CreateVersion7(), followSince, Clock);
        services.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await unitOfWork.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return ticket;
    }

    /// <summary>Carga el ticket con tracking, le aplica <paramref name="change"/> y lo guarda.</summary>
    public static async Task<SupportTicket> UpdateTicketAsync(
        QepApiFactory factory, Guid tenantId, SupportTicketId ticketId, Action<SupportTicket> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ticket = await services.GetRequiredService<ISupportTicketRepository>().FindAsync(tenantId, ticketId, Ct)
            ?? throw new InvalidOperationException($"Ticket '{ticketId}' was not found.");
        change(ticket);
        await services.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return ticket;
    }

    public static async Task<SupportTicket> FindTicketAsync(QepApiFactory factory, Guid tenantId, SupportTicketId ticketId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().FindAsync(tenantId, ticketId, Ct)
            ?? throw new InvalidOperationException($"Ticket '{ticketId}' was not found.");
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
