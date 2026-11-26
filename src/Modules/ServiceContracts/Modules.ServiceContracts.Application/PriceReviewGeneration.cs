using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// La generación que corre toda acción que resuelve una revisión, en su misma transacción (spec reajuste §6.4): así los cobros
/// liberados nacen al confirmar y no al día siguiente. Corre en modo <see cref="GenerationMode.Run"/>, como la corrida diaria,
/// y se llama siempre después de la mutación: si no hay nada que generar no hace nada. La versión no se sube acá porque la
/// acción ya la subió.
/// </summary>
internal static class PriceReviewGeneration
{
    public static void Run(
        ServiceContract service,
        ServicePriceSchedule? pending,
        DateOnly today,
        DateTimeOffset now,
        IChargeRepository charges,
        IServicePriceChangeRepository priceChanges)
    {
        var generated = ChargeGenerator.Generate(service, pending, GenerationMode.Run, today, now);
        charges.AddRange(generated.Charges);
        foreach (var change in generated.PriceChanges)
        {
            priceChanges.Add(change);
        }
    }
}
