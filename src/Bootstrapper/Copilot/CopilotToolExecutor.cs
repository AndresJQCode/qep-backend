using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper.Copilot;

/// <summary>Una llamada a una herramienta del copiloto, ya autorizada. <see cref="Dispatcher"/> es el del scope delegado: lo que despacha
/// corre como el miembro. <see cref="Permissions"/> son los resueltos para esta llamada (el alcance <c>own|all</c> de un resumen sale de
/// ahí). El tenant es el de <see cref="Principal"/>: el adaptador nunca lo toma de <see cref="Arguments"/>.</summary>
internal sealed record CopilotToolCall(
    JsonElement Arguments, CopilotPrincipal Principal, IRequestDispatcher Dispatcher, IReadOnlyCollection<string> Permissions);

/// <summary>Un adaptador de herramienta del copiloto: su contrato (Ai.Application) y cómo arma y despacha <b>la misma consulta</b> que su
/// endpoint. Sólo consultas: una prueba de arquitectura lo amarra (D-C4).</summary>
internal interface ICopilotTool
{
    CopilotToolContract Contract { get; }

    /// <summary>Un argumento inválido sale como <see cref="CopilotArgumentException"/> (o la <c>ValidationException</c> del handler).</summary>
    Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken);
}

/// <summary>Un argumento del modelo que no sirve (tipo, rango, fecha, id con formato). Vuelve al modelo como <c>invalid_arguments</c>
/// para que corrija.</summary>
internal sealed class CopilotArgumentException(string argument) : Exception($"The argument '{argument}' is not valid.")
{
    public string Argument { get; } = argument;
}

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C3 y plan fase 2 C2.4: el único lugar que corre consultas como el miembro que preguntó. En cada llamada:
/// <list type="number">
/// <item>resuelve los permisos del miembro con <see cref="IAuthorizationService.ResolvePermissionsAsync"/> (<c>null</c> →
/// <c>member_inactive</c>);</item>
/// <item>exige el permiso y el módulo de la herramienta (si no, <c>no_permission</c>: el modelo recibe el error, nunca el dato);</item>
/// <item>abre un scope de DI con <see cref="DelegatedExecutionContext"/> como <see cref="IExecutionContext"/>;</item>
/// <item>despacha con el <see cref="IRequestDispatcher"/> de ese scope, así el handler aplica sus propias reglas (alcance por asesor, costo
/// de producto, tenant) como si el miembro hubiera llamado al endpoint.</item>
/// </list>
/// Nunca con el principal de servicio de los agentes, nunca con un tenant que no sea el del hilo (D-C15).
/// </summary>
/// <remarks>
/// Los permisos y los módulos se resuelven con los servicios del scope de <b>cada llamada</b>, no con los del trabajo:
/// <c>AuthorizationService</c> y <c>MembershipDirectory</c> son scoped y el segundo memoiza la membresía por scope, así que con los del
/// trabajo la primera resolución valía para todas las herramientas siguientes (un miembro suspendido o sin el rol seguía consultando).
/// </remarks>
internal sealed class CopilotToolExecutor(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ICopilotTool> tools) : ICopilotToolExecutor
{
    private readonly Dictionary<string, ICopilotTool> _tools = tools.ToDictionary(tool => tool.Contract.Name, StringComparer.Ordinal);

    public async Task<IReadOnlyList<AiToolDefinition>?> ListAvailableAsync(CopilotPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var scope = scopeFactory.CreateAsyncScope();
        var permissions = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .ResolvePermissionsAsync(principal.UserId, principal.TenantId, cancellationToken);
        if (permissions is null)
        {
            return null;
        }

        var modules = await scope.ServiceProvider.GetRequiredService<ITenantModules>().FindAsync(principal.TenantId, cancellationToken);
        // Todas, sin recortar: el tope de 20 de Anthropic es de las estrictas, y éstas no lo son (CopilotReportTools.Contract).
        return
        [
            .. _tools.Values
                .Where(tool => Allows(permissions, modules, tool.Contract))
                .Select(tool => tool.Contract.Definition)
                .OrderBy(definition => definition.Name, StringComparer.Ordinal),
        ];
    }

    public async Task<CopilotToolResult> ExecuteAsync(
        CopilotPrincipal principal, string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            return CopilotToolResult.Error(AgentToolErrors.UnknownTool);
        }

        // D-C3: en cada llamada, no al empezar el trabajo, y en un scope propio (ver remarks), aparte del delegado de abajo: ese no debe
        // resolver nada antes de entrar al contexto del miembro. Lo que el miembro perdió entre dos herramientas, el copiloto también lo pierde.
        IReadOnlyCollection<string>? permissions;
        Modules.Tenancy.Domain.TenantModuleSet? modules;
        await using (var check = scopeFactory.CreateAsyncScope())
        {
            permissions = await check.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .ResolvePermissionsAsync(principal.UserId, principal.TenantId, cancellationToken);
            modules = permissions is null
                ? null
                : await check.ServiceProvider.GetRequiredService<ITenantModules>().FindAsync(principal.TenantId, cancellationToken);
        }

        if (permissions is null)
        {
            return CopilotToolResult.Error(CopilotToolErrors.MemberInactive);
        }

        if (!Allows(permissions, modules, tool.Contract))
        {
            return CopilotToolResult.Error(CopilotToolErrors.NoPermission);
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return CopilotToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return CopilotToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        // Antes de resolver nada más del scope: todo lo que lea IExecutionContext aquí adentro ve al miembro.
        scope.ServiceProvider.GetRequiredService<DelegatedExecutionScope>()
            .Enter(new DelegatedExecutionContext(principal.UserId, principal.TenantId, permissions));
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        try
        {
            return await tool.ExecuteAsync(new CopilotToolCall(arguments, principal, dispatcher, permissions), cancellationToken);
        }
        catch (Exception exception) when (exception is CopilotArgumentException or ValidationException or DomainException)
        {
            return CopilotToolResult.Error(AgentToolErrors.InvalidArguments);
        }
        catch (RequestForbiddenException)
        {
            // La regla del handler (o su guarda de módulo) dijo que no: para el modelo es lo mismo que no tener el permiso.
            return CopilotToolResult.Error(CopilotToolErrors.NoPermission);
        }
        catch (ResourceNotFoundException)
        {
            return CopilotToolResult.Error(CopilotToolErrors.NotFound);
        }
    }

    /// <summary>El permiso en los resueltos (ya enmascarados por módulo) y, además, el módulo efectivo: fail closed sin fila de tenant.</summary>
    private static bool Allows(IReadOnlyCollection<string> permissions, Modules.Tenancy.Domain.TenantModuleSet? modules, CopilotToolContract contract) =>
        permissions.Contains(contract.Permission, StringComparer.Ordinal) && modules is not null && modules.IsEnabled(contract.Module);
}
