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
            SupportTicketPriority.Medium,
            new SupportConversationRef(conversationId, channel), customerId,
            new TicketRequester("Ana", "Gómez", null, null, null, null, "+573001234567", null, "CO", null),
            null, actorMemberId ?? Guid.CreateVersion7(), followSince, Clock);
        services.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await unitOfWork.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return ticket;
    }

    /// <summary>Fase 3: un ticket anónimo del formulario público (sin solicitante ni cliente, se responde por la página). Guarda una
    /// versión del formulario si el tenant no tiene, para que el ticket apunte a una que existe.</summary>
    public static async Task<SupportTicket> SaveAnonymousTicketAsync(QepApiFactory factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenantId, Ct);
        var type = (await services.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenantId, Ct))[0];
        var forms = services.GetRequiredService<ISupportFormRepository>();
        var current = await forms.CurrentAsync(tenantId, Ct);
        var form = current ?? SupportFormVersion.Create(
            Guid.CreateVersion7(), tenantId, 1, isPublished: true, showDocument: true, showAddress: true, showLandline: true,
            businessNameRequired: true, allowAttachments: false, null, null, null, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        if (current is null)
        {
            forms.Add(form);
        }

        var unitOfWork = services.GetRequiredService<ISupportUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        var number = await services.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenantId, Ct);
        var ticket = SupportTicket.CreateFromPublicForm(
            SupportTicketId.New(), tenantId, number, type, "Denuncia anónima", "Quiero reportar algo sin dar mis datos.",
            requester: null, customerId: null, form.Id, DateTimeOffset.UtcNow, Clock);
        services.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await unitOfWork.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return ticket;
    }

    /// <summary>Spec 2026-11-22: una tarea interna guardada por el dominio y el repositorio. Aprovisiona el tenant (con su tipo interno)
    /// y radica con el generador real. <paramref name="now"/> es la hora de radicación: un host con reloj propio pasa la suya, porque una
    /// fecha límite se valida contra ella y no contra el reloj real.</summary>
    public static async Task<SupportTicket> SaveInternalTicketAsync(
        QepApiFactory factory, Guid tenantId, Guid creatorMemberId, Guid assigneeMemberId, DateOnly? dueOn = null,
        DateTimeOffset? now = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenantId, Ct);
        var type = await services.GetRequiredService<ISupportTicketTypeRepository>().FindInternalAsync(tenantId, Ct)
            ?? throw new InvalidOperationException("The tenant has no internal ticket type.");
        var unitOfWork = services.GetRequiredService<ISupportUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        var number = await services.GetRequiredService<ISupportTicketNumberGenerator>().NextAsync(tenantId, Ct);
        var ticket = SupportTicket.CreateInternal(
            SupportTicketId.New(), tenantId, number, type, "Cuadrar el inventario", "Revisar las diferencias de octubre.",
            SupportTicketPriority.Medium, assigneeMemberId, dueOn, creatorMemberId, now ?? DateTimeOffset.UtcNow, Clock);
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
