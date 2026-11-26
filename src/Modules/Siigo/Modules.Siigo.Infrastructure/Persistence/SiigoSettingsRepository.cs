using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>Con tracking: el llamador modifica y guarda con <c>ISiigoUnitOfWork</c>. Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class SiigoSettingsRepository(SiigoDbContext dbContext) : ISiigoSettingsRepository
{
    public async Task<SiigoSettings?> FindOrCreateAsync(
        Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken)
    {
        // Spec 2026-11-18 §5.2: la fila nace vacía al primer acceso. ON CONFLICT DO NOTHING, no «buscar y agregar»: dos primeros
        // accesos a la vez (la pantalla pide la configuración y las listas en paralelo) no chocan, y los dos leen la misma fila. Las
        // columnas que no se nombran toman su default de la tabla: la DIAN, el correo y el flete aparte, apagados, y la versión en 0.
        await dbContext.Database.ExecuteSqlAsync(
            $"INSERT INTO siigo.settings (connection_id, tenant_id) VALUES ({connectionId}, {tenantId}) ON CONFLICT (connection_id) DO NOTHING",
            cancellationToken);

        var query = dbContext.Settings
            .Include(settings => settings.TaxLinks)
            .Include(settings => settings.SellerLinks)
            .Include(settings => settings.WithholdingLinks)
            .Include(settings => settings.PaymentLinks)
            .AsSplitQuery();
        if (withVariantLinks)
        {
            query = query.Include(settings => settings.VariantLinks);
        }

        // Una fila con ese connection_id y otro tenant no es de este tenant: null (el llamador responde 403, nunca la crea ni la lee).
        return await query.SingleOrDefaultAsync(
            settings => settings.ConnectionId == connectionId && settings.TenantId == tenantId, cancellationToken);
    }

    public async Task<SiigoSettings?> FindByCompanyAsync(
        Guid tenantId, Guid companyId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        // Sólo lectura: la factura no modifica la configuración. Los vínculos de productos se traen sólo para las variantes del pedido
        // (la tabla llega a miles de filas por conexión).
        var settings = await dbContext.Settings
            .Include(value => value.TaxLinks)
            .Include(value => value.SellerLinks)
            .Include(value => value.WithholdingLinks)
            .Include(value => value.PaymentLinks)
            .AsSplitQuery()
            .SingleOrDefaultAsync(value => value.TenantId == tenantId && value.CompanyId == companyId, cancellationToken);
        if (settings is not null && variantIds.Count > 0)
        {
            await dbContext.VariantLinks
                .Where(link => link.ConnectionId == settings.ConnectionId && variantIds.Contains(link.VariantId))
                .LoadAsync(cancellationToken);
        }

        return settings;
    }

    public void Remove(SiigoSettings settings) => dbContext.Settings.Remove(settings);
}
