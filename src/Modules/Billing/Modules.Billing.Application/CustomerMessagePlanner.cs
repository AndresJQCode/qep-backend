using Modules.Billing.Domain;

namespace Modules.Billing.Application;

/// <summary>
/// El mensaje del día a un cliente. <c>Charges</c> son las líneas que lista; <c>Steps</c>, los pasos que cubre (una fila en
/// <c>reminder_steps</c> por cada uno); <c>NearestDueDate</c>, el vencimiento más próximo de lo listado.
/// </summary>
public sealed record CustomerMessagePlan(
    BillingMessageKind Kind,
    IReadOnlyList<Charge> Charges,
    IReadOnlyList<(Guid ChargeId, string Step)> Steps,
    DateOnly NearestDueDate);

/// <summary>Qué le toca hoy a un cliente (spec §3.3, paso 4). Puro: el procesador le pasa los cobros y los pasos ya cubiertos.</summary>
public static class CustomerMessagePlanner
{
    public static CustomerMessagePlan? Plan(
        IReadOnlyList<Charge> pendingOfCustomer, IReadOnlySet<(Guid ChargeId, string Step)> covered, DateOnly today)
    {
        var pending = pendingOfCustomer
            .Where(charge => charge.Status == ChargeStatus.Pending)
            .OrderBy(charge => charge.DueDate)
            .ThenBy(charge => charge.Id)
            .ToArray();

        var triggered = new List<(Charge Charge, string Step)>();
        foreach (var charge in pending)
        {
            // Ruling F8: el último paso con fecha <= hoy; si ya está cubierto, ese cobro no dispara nada hoy.
            if (ReminderCalendar.StepDueOn(charge.DueDate, today) is { } step && !covered.Contains((charge.Id, step)))
            {
                triggered.Add((charge, step));
            }
        }

        if (triggered.Count == 0)
        {
            return null;
        }

        var steps = triggered.Select(entry => (entry.Charge.Id, entry.Step)).ToArray();

        // Overdue lista todos los vencidos del cliente, no sólo los que dispararon: el cliente ve su deuda entera en un mensaje.
        if (triggered.Any(entry => entry.Charge.DueDate < today))
        {
            var overdue = pending.Where(charge => charge.DueDate < today).ToArray();
            return new CustomerMessagePlan(BillingMessageKind.Overdue, overdue, steps, overdue[0].DueDate);
        }

        var upcoming = triggered.Select(entry => entry.Charge).ToArray();
        return new CustomerMessagePlan(BillingMessageKind.Reminder, upcoming, steps, upcoming[0].DueDate);
    }
}
