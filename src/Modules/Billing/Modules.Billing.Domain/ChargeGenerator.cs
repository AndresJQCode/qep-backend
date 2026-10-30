namespace Modules.Billing.Domain;

public static class ChargeGenerator
{
    /// <summary>Cuántos días por delante de hoy se generan los cobros (spec §3.2).</summary>
    public const int WindowDays = 5;

    /// <summary>
    /// Genera los cobros que faltan hasta <c>today + WindowDays</c> y avanza el servicio
    /// (<see cref="BillingService.MarkGenerated"/>). Un servicio que aún no generó nada genera al
    /// menos el primero aunque caiga fuera de la ventana: la pantalla necesita el próximo vencimiento.
    /// </summary>
    public static IReadOnlyList<Charge> Generate(BillingService service, DateOnly today, DateTimeOffset now)
    {
        var limit = today.AddDays(WindowDays);
        var charges = new List<Charge>();

        while (service.NextDueDateToGenerate() is { } next
            && (next <= limit || service.LastGeneratedDueDate is null))
        {
            charges.Add(Charge.Generate(service, next, now));
            service.MarkGenerated(next);
        }

        return charges;
    }
}
