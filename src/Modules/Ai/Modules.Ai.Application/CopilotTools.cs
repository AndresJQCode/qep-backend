using System.Text.Json;
using Modules.Ai.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-11-17 (copiloto) §6: los nombres que ve el modelo. Son contrato con el prompt y con <c>progress</c> del mensaje
/// pendiente, que el frontend muestra.</summary>
public static class CopilotToolNames
{
    /// <summary>Herramienta final: la respuesta con sus citas.</summary>
    public const string Answer = "answer";

    /// <summary>Herramienta final: no hay con qué contestar; se muestra como una respuesta más.</summary>
    public const string CannotAnswer = "cannot_answer";
}

/// <summary>D-C6: los <c>kind</c> de una cita. El frontend arma el enlace con el kind y el id; el diccionario de rutas es suyo.</summary>
public static class CopilotCitationKinds
{
    public const string Order = "order";
    public const string Quotation = "quotation";
    public const string Customer = "customer";
    public const string Product = "product";
    public const string Loan = "loan";
    public const string Service = "service";
    public const string Charge = "charge";

    public static IReadOnlyList<string> All { get; } = [Order, Quotation, Customer, Product, Loan, Service, Charge];
}

/// <summary>Los errores de herramienta propios del copiloto, además de los comunes de <see cref="AgentToolErrors"/>.</summary>
public static class CopilotToolErrors
{
    /// <summary>D-C3: el miembro no tiene, ahora, el permiso o el módulo de la herramienta. El modelo lo lee y responde con lo que tiene.</summary>
    public const string NoPermission = "no_permission";

    /// <summary>D-C3: el miembro ya no tiene membresía activa. Termina el trabajo: la respuesta queda <c>Failed</c> con este código.</summary>
    public const string MemberInactive = "member_inactive";

    /// <summary>El registro pedido no existe en el tenant (o el miembro no lo ve).</summary>
    public const string NotFound = "not_found";
}

/// <summary>§6: lo que el modelo ve de una herramienta (<paramref name="Definition"/>, estricta: todos los campos en <c>required</c> y sin
/// propiedades extra) y lo que exige para ofrecerse y correr: el permiso del miembro y el módulo del tenant, mirados en cada llamada.
/// Es <b>además</b> de lo que la consulta ya verifica.</summary>
public sealed record CopilotToolContract(AiToolDefinition Definition, string Permission, TenantModuleKey Module)
{
    public string Name => Definition.Name;
}

/// <summary>D-C3 y D-C15: con quién corre una herramienta. Sale del hilo (dueño y tenant), nunca de un argumento del modelo.</summary>
public sealed record CopilotPrincipal(Guid TenantId, Guid UserId);

/// <summary>Lo que devolvió una herramienta del copiloto.</summary>
/// <param name="ResultJson">Lo que lee el modelo; con <paramref name="IsError"/>, <c>{"error":"…"}</c>.</param>
/// <param name="Refs">D-C6: los registros citables de este resultado; son la única fuente de las citas de <c>answer</c>.</param>
public sealed record CopilotToolResult(string ResultJson, bool IsError, string? ErrorCode, IReadOnlyList<CopilotCitation> Refs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CopilotToolResult Ok(object value, IReadOnlyList<CopilotCitation> refs) =>
        new(JsonSerializer.Serialize(value, Json), false, null, refs);

    public static CopilotToolResult Error(string code) => new(JsonSerializer.Serialize(new { error = code }, Json), true, code, []);
}

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C3 y §6: las herramientas de sólo lectura, ejecutadas <b>como el miembro</b>. El adaptador vive en
/// Bootstrapper (Ai no ve los otros módulos): resuelve los permisos del miembro en cada llamada, exige el permiso y el módulo de la
/// herramienta y despacha la misma consulta del endpoint con un contexto de ejecución delegado.
/// </summary>
public interface ICopilotToolExecutor
{
    /// <summary>Las herramientas que el miembro puede usar ahora (permiso y módulo), ordenadas por nombre; <c>null</c> si no tiene membresía
    /// activa en el tenant.</summary>
    Task<IReadOnlyList<AiToolDefinition>?> ListAvailableAsync(CopilotPrincipal principal, CancellationToken cancellationToken);

    /// <summary>Una llamada. Nunca lanza por permisos ni por argumentos: vuelven como <see cref="CopilotToolResult.Error"/>.</summary>
    Task<CopilotToolResult> ExecuteAsync(CopilotPrincipal principal, string toolName, string argumentsJson, CancellationToken cancellationToken);
}
