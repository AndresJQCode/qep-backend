namespace Modules.ServiceContracts.Domain;

/// <summary>Lo que dejó una pasada de generación.</summary>
/// <param name="PriceChanges">La fila del historial del cambio programado que se aplicó, si alguno; quien llama la persiste.</param>
/// <param name="ServiceChanged">
/// El contrato cambió: generó, cambió su retención, avanzó la revisión o aplicó un cambio programado. Con esto la corrida decide
/// si guarda.
/// </param>
public sealed record GenerationResult(
    IReadOnlyList<Charge> Charges,
    IReadOnlyList<ServicePriceChange> PriceChanges,
    bool ServiceChanged);

public static class ChargeGenerator
{
    /// <summary>Cuántos días por delante de hoy se generan los cobros (spec §3.2).</summary>
    public const int WindowDays = 5;

    /// <summary>La generación de crear, importar y la vista previa, sin cambio programado: sólo los cobros.</summary>
    public static IReadOnlyList<Charge> Generate(ServiceContract service, DateOnly today, DateTimeOffset now) =>
        Generate(service, null, GenerationMode.Creation, today, now).Charges;

    /// <summary>
    /// Genera los cobros que faltan hasta <c>today + WindowDays</c> y avanza el servicio
    /// (<see cref="ServiceContract.MarkGenerated"/>). Un servicio que aún no generó nada genera al
    /// menos el primero aunque caiga fuera de la ventana: la pantalla necesita el próximo vencimiento.
    /// Por cada período (spec reajuste §6.3): aplica el cambio programado que lo alcanza; en <see cref="GenerationMode.Run"/>,
    /// una revisión sin resolver retiene (<c>Ipc</c>, y corta ahí) o genera «sin revisar» (<c>Manual</c>); y lo que estuvo
    /// retenido vence en <c>max(período, hoy + WindowDays)</c>, sin mover el período ni los siguientes.
    /// </summary>
    /// <param name="pending">El cambio programado pendiente del contrato; queda <c>Applied</c> si la pasada lo alcanza.</param>
    public static GenerationResult Generate(
        ServiceContract service,
        ServicePriceSchedule? pending,
        GenerationMode mode,
        DateOnly today,
        DateTimeOffset now)
    {
        var limit = today.AddDays(WindowDays);
        var heldSince = service.HeldSince;
        var reviewDate = service.NextReviewDate;
        var charges = new List<Charge>();
        var priceChanges = new List<ServicePriceChange>();
        var held = false;

        while (service.NextPeriodToGenerate() is { } period
            && (period <= limit || service.LastGeneratedPeriodDate is null))
        {
            if (pending is { Status: PriceScheduleStatus.Pending } && period >= pending.EffectiveFrom)
            {
                priceChanges.Add(service.ApplyScheduledPrice(pending, period, now));
                if (period >= service.NextReviewDate)
                {
                    service.AdvanceReview(period);
                }
            }

            // Un cambio que cubriera esta revisión ya se aplicó arriba y la dejó atrás.
            var unreviewed = false;
            if (mode == GenerationMode.Run && period >= service.NextReviewDate)
            {
                if (service.AdjustmentMode == AdjustmentMode.Ipc)
                {
                    held = true;
                    break;
                }

                unreviewed = true;
                service.AdvanceReview(period);
            }

            // «Retenido al empezar» la pasada: todo lo que salga de ella le deja al cliente sus días para pagar.
            var due = heldSince is not null && limit > period ? limit : period;
            charges.Add(Charge.Generate(service, period, due, unreviewed, now));
            service.MarkGenerated(period);
        }

        if (!held)
        {
            service.ReleaseHold();
        }
        else if (charges.Count > 0 || service.HeldSince is null)
        {
            // Liberar y volver a topar con una revisión en la misma pasada es una retención nueva.
            service.Hold(today);
        }

        var changed = charges.Count > 0
            || priceChanges.Count > 0
            || service.HeldSince != heldSince
            || service.NextReviewDate != reviewDate;
        return new GenerationResult(charges, priceChanges, changed);
    }
}
