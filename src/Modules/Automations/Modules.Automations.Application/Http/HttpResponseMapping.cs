using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Application.Http;

/// <summary>
/// Spec 2026-10-21 D-H7 y ruling P2-R36: lo que el mapeo del bloque HTTP guarda de una respuesta ya leída como JSON. Es el mismo paso para
/// la respuesta de la API (<c>HttpBlockExecutor</c>) y para la respuesta de ejemplo del simulador (D-S2): si fueran dos copias, el simulador
/// podría mostrar un mapeo que en producción no pasa.
/// </summary>
public static class HttpResponseMapping
{
    /// <summary>Cada <c>from</c> sobre <paramref name="response"/> (<c>$</c>, <c>$[0]</c>, <c>[0].id</c> o por puntos) va a su <c>to</c>. Una
    /// ruta ausente deja la variable en <c>null</c>, que no es un fallo: la Condición siguiente lo mira.</summary>
    public static RunVariables Apply(IReadOnlyList<HttpMapping> mapping, JsonNode? response)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var mapped = RunVariables.Empty();
        foreach (var pair in mapping)
        {
            mapped.Set(pair.To, JsonPath.SelectResponse(response, pair.From));
        }

        return mapped;
    }
}
