using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-20 §3.1: «Talla» y «Color», inactivas, en el primer acceso a la biblioteca. Mismo
/// esquema que <c>PipelineProvisioner</c>: una transacción propia con SQL crudo —para no arrastrar
/// cambios que el contexto ya tuviera rastreados— y una marca por tenant en
/// <c>catalog.option_library_provisions</c> con <c>ON CONFLICT (tenant_id) DO NOTHING</c>. Dos
/// listados a la vez esperan la clave primaria: uno siembra y el otro no inserta nada.
///
/// La marca existe porque el <c>ON CONFLICT</c> de las opciones solo no alcanza: si el tenant borra
/// «Talla» (sin uso se puede), no queda fila con qué chocar y volvería en el siguiente listado. Y
/// cada opción se inserta con <c>ON CONFLICT DO NOTHING</c> por si el tenant ya creó una «talla»
/// propia antes de abrir la biblioteca: la suya gana y sus valores no se tocan.
/// </summary>
internal sealed class OptionLibraryProvisioner(CatalogDbContext dbContext, IClock clock) : IOptionLibraryProvisioner
{
    internal static readonly IReadOnlyList<(string Name, string[] Values)> Seed =
    [
        ("Talla", ["XS", "S", "M", "L", "XL", "XXL"]),
        ("Color", ["Negro", "Blanco", "Gris", "Azul", "Rojo", "Verde", "Amarillo", "Café", "Beige", "Rosado"]),
    ];

    public async Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // El camino de todos los días: una lectura y nada más.
        var provisioned = await dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM catalog.option_library_provisions WHERE tenant_id = {tenantId}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (provisioned)
        {
            return;
        }

        var now = clock.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var marked = await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO catalog.option_library_provisions (tenant_id, provisioned_at)
            VALUES ({tenantId}, {now})
            ON CONFLICT (tenant_id) DO NOTHING
            """, cancellationToken);
        if (marked == 1)
        {
            foreach (var (name, values) in Seed)
            {
                // Por el agregado y no a mano: los valores salen normalizados y con sus ids.
                var option = OptionDefinition.Create(tenantId, name, values, isActive: false, now);
                var inserted = await dbContext.Database.ExecuteSqlAsync($"""
                    INSERT INTO catalog.option_definitions (id, tenant_id, name, is_active, version, created_at, updated_at)
                    VALUES ({option.Id.Value}, {tenantId}, {option.Name}, FALSE, 1, {now}, {now})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
                if (inserted == 0)
                {
                    continue;
                }

                foreach (var value in option.Values)
                {
                    await dbContext.Database.ExecuteSqlAsync($"""
                        INSERT INTO catalog.option_values (id, option_definition_id, value, position, is_active)
                        VALUES ({value.Id}, {option.Id.Value}, {value.Value}, {value.Position}, TRUE)
                        """, cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
