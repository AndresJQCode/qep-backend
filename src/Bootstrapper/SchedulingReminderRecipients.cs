using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Scheduling.Application;

namespace Bootstrapper;

/// <summary>
/// Customers → Scheduling para el recordatorio de cita (plan 2026-11-21, PD-7 y Tarea 12). La Agenda no referencia Clientes: este adaptador
/// traduce el cliente a lo que el recordatorio decide —teléfono E.164, si está activo, si su último consentimiento de datos es una
/// revocación y si tiene <c>MarketingOptOut</c> en su ficha— y deja la decisión a la Agenda. Sin SQL a mano: usa los repositorios públicos de
/// Clientes, así que renombrar una columna no lo rompe en silencio.
/// <para>
/// El consentimiento vigente es la última fila por <c>(occurred_at, sequence)</c> (<see cref="ICustomerDataConsentRepository.FindLatestAsync"/>),
/// nunca sólo <c>sequence</c>. La consulta es por cliente, no por identidad de canal: una revocación registrada por otro medio
/// (<c>Medium = Other</c>, sin conversación ni canal; <c>CK_data_consents_identity</c>) también cuenta, que es lo que la ley pide —el titular
/// la pidió, no «en esta conversación»—. Si el cliente fue fusionado, se lee el que quedó (un salto: <c>MergedIntoId</c> apunta siempre a un
/// no fusionado) y la fusión ya movió sus filas de consentimiento allá. Toda lectura va acotada al tenant.
/// </para>
/// </summary>
internal sealed class SchedulingReminderRecipients(ICustomerRepository customers, ICustomerDataConsentRepository consents) : ISchedulingReminderRecipients
{
    public async Task<ReminderRecipient?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        if (customer is null)
        {
            return null;
        }

        // El que quedó tras una fusión es quien tiene el teléfono, el consentimiento y el MarketingOptOut vigentes.
        if (customer.MergedIntoId is { } survivorId)
        {
            customer = await customers.FindAsync(tenantId, survivorId, cancellationToken);
            if (customer is null)
            {
                return null;
            }
        }

        var latest = await consents.FindLatestAsync(tenantId, customer.Id, cancellationToken);
        return new ReminderRecipient(
            customerId,
            customer.Name,
            customer.PhoneE164,
            customer.IsActive,
            ConsentRevoked: latest?.Event == DataConsentEvent.Revoked,
            customer.MarketingOptOut);
    }
}
