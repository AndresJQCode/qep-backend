namespace Modules.Support.Domain;

/// <summary>Cómo va el plazo de un ticket, por nombre en el DTO (<c>SupportClockDto.DueState</c>).</summary>
public enum SupportDueState
{
    NoTerm,
    OnTrack,
    DueSoon,
    Overdue,
    Paused,
    Finished,
}

public static class SupportDueStates
{
    /// <summary>Un estado que detiene el reloj manda sobre todo lo demás, también sobre «sin término» (pre-flight R11):
    /// <see cref="SupportDueState.Paused"/> esperando al cliente, <see cref="SupportDueState.Finished"/> resuelto o cerrado. Con
    /// el reloj corriendo: sin vencimiento es <see cref="SupportDueState.NoTerm"/>; después del vencimiento,
    /// <see cref="SupportDueState.Overdue"/>; si quedan <paramref name="dueSoonBusinessDays"/> días hábiles o menos (contando el
    /// del vencimiento, que es 0), <see cref="SupportDueState.DueSoon"/>. El umbral es el del tenant [owner 2026-11-14] y 0 apaga
    /// «por vencer».</summary>
    public static (SupportDueState State, int? BusinessDaysLeft) Evaluate(
        SupportTicketStatus status, DateOnly? dueOn, DateOnly today, ISupportBusinessCalendar calendar, int dueSoonBusinessDays)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (status is SupportTicketStatus.WaitingOnCustomer)
        {
            return (SupportDueState.Paused, null);
        }

        if (status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            return (SupportDueState.Finished, null);
        }

        if (dueOn is not { } due)
        {
            return (SupportDueState.NoTerm, null);
        }

        if (today > due)
        {
            return (SupportDueState.Overdue, 0);
        }

        var left = today == due ? 0 : SupportBusinessDays.CountStrictlyBetween(calendar, today, due) + 1;
        var dueSoon = dueSoonBusinessDays > 0 && left <= dueSoonBusinessDays;
        return (dueSoon ? SupportDueState.DueSoon : SupportDueState.OnTrack, left);
    }
}
