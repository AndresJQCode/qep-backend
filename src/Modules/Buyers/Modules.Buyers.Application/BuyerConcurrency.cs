using BuildingBlocks.Application;

namespace Modules.Buyers.Application;

/// <summary>
/// M2: una escritura sobre la cuenta que choca con otra (el xmin cambió, o la purga la borró) se descarta y se intenta una vez
/// más desde la lectura, con lo que ahora hay en la base. El intento tiene que releer la cuenta: el tracker queda limpio. Un
/// segundo choque sube tal cual (412).
/// </summary>
internal static class BuyerConcurrency
{
    public static async Task<T> RetryOnceAsync<T>(IBuyersUnitOfWork unitOfWork, Func<Task<T>> attempt)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(attempt);

        try
        {
            return await attempt();
        }
        catch (RequestConcurrencyException)
        {
            unitOfWork.DiscardPendingChanges();
            return await attempt();
        }
    }
}
