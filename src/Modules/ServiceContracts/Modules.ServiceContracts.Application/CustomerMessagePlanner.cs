using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// El mensaje del día a un cliente. <c>Charges</c> son las líneas que lista; <c>Steps</c>, los pasos que cubre (una fila en
/// <c>reminder_steps</c> por cada uno); <c>NearestDueDate</c>, el vencimiento más próximo de lo listado.
/// </summary>
public sealed record CustomerMessagePlan(
    ServiceContractsMessageKind Kind,
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

        // Overdue lista todos los vencidos del cliente, no sólo los que dispararon: el cliente ve su deuda entera en un mensaje.
        // Pero cubre sólo los pasos de los vencidos (owner, 2026-10-30): el before_5 o due de un cobro por vencer no se menciona
        // en ese mensaje, y marcarlo cubierto perdería el aviso. Queda sin fila y sale como Reminder el día siguiente, porque
        // ReminderCalendar.StepDueOn sigue devolviendo ese paso mientras sea el último con fecha <= hoy.
        if (triggered.Any(entry => entry.Charge.DueDate < today))
        {
            var overdue = pending.Where(charge => charge.DueDate < today).ToArray();
            var overdueSteps = triggered
                .Where(entry => entry.Charge.DueDate < today)
                .Select(entry => (entry.Charge.Id, entry.Step))
                .ToArray();
            return new CustomerMessagePlan(ServiceContractsMessageKind.Overdue, overdue, overdueSteps, overdue[0].DueDate);
        }

        var upcoming = triggered.Select(entry => entry.Charge).ToArray();
        var steps = triggered.Select(entry => (entry.Charge.Id, entry.Step)).ToArray();
        return new CustomerMessagePlan(ServiceContractsMessageKind.Reminder, upcoming, steps, upcoming[0].DueDate);
    }
}
