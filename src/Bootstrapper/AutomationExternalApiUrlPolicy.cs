using Modules.Automations.Application;
using Modules.Integrations.Infrastructure.ExternalApi;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H2 y fallos P2-R30/P2-R31: el bloque HTTP del chatbot juzga la URL y el header con la misma política que el
/// probador de la conexión (<see cref="ExternalApiUrlPolicy"/>); Automations no referencia Integrations.</summary>
internal sealed class AutomationExternalApiUrlPolicy : IExternalApiUrlPolicy
{
    public string? Reject(Uri url) => ExternalApiUrlPolicy.Reject(url);

    public bool IsReservedHeader(string name) => ExternalApiUrlPolicy.IsReservedHeader(name);

    /// <summary><see cref="ExternalApiUrlPolicy.CreateHandler"/> lanza <see cref="ExternalApiUrlRejectedException"/> desde
    /// <c>ConnectCallback</c>, y <c>HttpClient</c> la entrega envuelta (a veces en más de un nivel).</summary>
    public string? RejectionIn(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is ExternalApiUrlRejectedException rejection)
            {
                return rejection.Reason;
            }
        }

        return null;
    }
}
