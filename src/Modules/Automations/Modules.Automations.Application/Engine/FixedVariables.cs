using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

/// <summary>Spec 2026-10-21 D-M8: las variables con que nace toda corrida, en el orden de <c>FlowFixedVariables.Names</c>. Un dato que no
/// se conoce (perfil sin nombre, empresa sin nombre visible) va vacío, como una variable que no existe al interpolar.</summary>
public static class FixedVariables
{
    public static RunVariables Build(string? customerName, string? phone, string? tenantName, string connectionName)
    {
        var variables = RunVariables.Empty();
        variables.Set("nombre_contacto", JsonValue.Create(customerName ?? string.Empty));
        variables.Set("telefono", JsonValue.Create(phone ?? string.Empty));
        variables.Set("empresa", JsonValue.Create(tenantName ?? string.Empty));
        variables.Set("conexion", JsonValue.Create(connectionName ?? string.Empty));
        return variables;
    }
}
