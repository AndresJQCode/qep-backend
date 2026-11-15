namespace Modules.Support.Application;

/// <summary>
/// El logo público del tenant para el formulario (plan Task 5), con adaptador en Bootstrapper sobre Tenancy y Storage (calco de
/// <c>StoreTenantLookup</c>). Null sin logo, con un logo sin publicar o con uno de otro tenant.
/// </summary>
public interface ISupportTenantBranding
{
    Task<string?> LogoUrlAsync(Guid tenantId, CancellationToken cancellationToken);
}
