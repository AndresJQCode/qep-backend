namespace Modules.Authorization.Application;

public sealed record AuthorizationDecision(bool Allowed, string ReasonCode)
{
    public static AuthorizationDecision Allow() => new(true, "allowed");

    public static AuthorizationDecision Deny(string reasonCode) => new(false, reasonCode);
}

/// <summary>
/// El contrato público de la capacidad Authorization (capability-contracts.md). Las
/// decisiones de acceso son deny por defecto y siempre acotadas al tenant: los permisos de
/// un sujeto vienen de los roles de su membresía activa en el tenant. El frontend nunca es
/// un punto de enforcement.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>Decide si el sujeto puede ejecutar <paramref name="permission"/>
    /// en el tenant. Deny por defecto; deniega cuando no hay membresía activa.</summary>
    Task<AuthorizationDecision> AuthorizeAsync(
        Guid subjectId,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken);

    /// <summary>Resuelve los permisos efectivos del sujeto en el tenant, o
    /// <c>null</c> cuando el sujeto no tiene membresía activa ahí.</summary>
    Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(
        Guid subjectId,
        Guid tenantId,
        CancellationToken cancellationToken);
}
