using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las pruebas de persistencia que migran sólo el esquema de Quotations no tienen <c>customers.customers</c>, y desde borrar
/// clientes (spec 2026-11-10, P1/P2) <see cref="QuotationsUnitOfWork"/> comprueba que el cliente de cada cotización o pedido
/// nuevo exista antes de guardarlo (<c>CustomerReferenceWrites</c>). Esta réplica arma la tabla con las dos columnas que esa
/// comprobación lee y da de alta a los clientes de lo que se va a insertar, y después guarda por el UoW real.
/// </summary>
internal static class CustomerReferenceReplica
{
    public static async Task SaveAsync(QuotationsDbContext context)
    {
        var clients = context.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity switch
            {
                Quotation quotation => (quotation.TenantId, quotation.ClientId),
                Order order => (order.TenantId, order.ClientId),
                _ => ((Guid, Guid)?)null
            })
            .OfType<(Guid TenantId, Guid ClientId)>()
            .Distinct()
            .ToArray();

        await context.Database.ExecuteSqlRawAsync(
            "CREATE SCHEMA IF NOT EXISTS customers; "
            + "CREATE TABLE IF NOT EXISTS customers.customers (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);",
            TestContext.Current.CancellationToken);
        foreach (var (tenantId, clientId) in clients)
        {
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO customers.customers (id, tenant_id) VALUES ({clientId}, {tenantId}) ON CONFLICT DO NOTHING",
                TestContext.Current.CancellationToken);
        }

        await new QuotationsUnitOfWork(context).SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
