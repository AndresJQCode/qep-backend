using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-08 §1 (O2). No se llama <c>PlatformOptions</c> para no confundirla con el módulo
/// <c>Modules.Platform</c> (log de fallas); la clave sí es <c>Platform:OperatorTenantId</c>, como la
/// aprobó el owner. Vive en Infrastructure por la misma razón que <see cref="EntitlementsOptions"/>:
/// ninguna capa Application usa <c>IOptions</c>.
/// </summary>
public sealed class OperatorTenantOptions
{
    public const string SectionName = "Platform";

    /// <summary>Opcional en todos los ambientes (D10): sin valor no hay consola y <c>operator.*</c> se
    /// descarta en todos los tenants. Con valor, nunca <see cref="Guid.Empty"/>.</summary>
    public Guid? OperatorTenantId { get; set; }
}

internal sealed class OperatorTenantOptionsValidator : IValidateOptions<OperatorTenantOptions>
{
    public ValidateOptionsResult Validate(string? name, OperatorTenantOptions options) =>
        options.OperatorTenantId == Guid.Empty
            ? ValidateOptionsResult.Fail(
                "Platform:OperatorTenantId must be a tenant id, not Guid.Empty. Leave it unset to disable the operator console.")
            : ValidateOptionsResult.Success;
}

internal sealed class OperatorTenant(IOptions<OperatorTenantOptions> options) : IOperatorTenant
{
    // Guid? contra Guid: sin valor configurado es false para todos.
    public bool IsOperator(Guid tenantId) => options.Value.OperatorTenantId == tenantId;
}
