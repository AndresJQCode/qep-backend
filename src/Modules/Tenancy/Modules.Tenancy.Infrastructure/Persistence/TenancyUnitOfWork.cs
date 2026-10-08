using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Npgsql;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenancyUnitOfWork(TenancyDbContext dbContext) : ITenancyUnitOfWork
{
    /// <summary>unique_violation de PostgreSQL.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Lo crea la migración inicial (20260705161840_InitialPlatform) sobre tenancy.tenants.
    /// Se hace match por nombre para que una colisión en memberships —que tiene su propio
    /// índice único— no se etiquete mal como un slug ya tomado.
    /// </summary>
    private const string TenantSlugIndex = "IX_tenants_slug";

    /// <summary>
    /// Índice único parcial del código de asesor (spec 2026-09-24, D3). Se reconoce por nombre, y
    /// no sólo por el 23505, porque memberships tiene otros índices únicos —(user_id, tenant_id) e
    /// invitation_token_hash— y etiquetarlos como "código tomado" mandaría a corregir el campo
    /// equivocado.
    /// </summary>
    private const string AdvisorCodeIndex = "IX_memberships_tenant_id_advisor_code";

    /// <summary>PK de tenant_modules (AddTenantModules). Se reconoce por nombre, no sólo por el 23505.</summary>
    private const string TenantModulesPrimaryKey = "PK_tenant_modules";

    public async Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Advisory lock acotado a la transacción: una sola base con varios esquemas, así que
        // Identity y Tenancy comparten el espacio de locks aunque no compartan DbContext ni
        // conexión. SaveChangesAsync se suma a esta transacción sin abrir otra.
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({UserLifecycleLockKey.For(email)}))",
            cancellationToken);
        return new TransactionScope(transaction);
    }

    public async Task<ITenantChangeScope> BeginTenantChangeScopeAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({TenantChangeLock.KeyFor(tenantId.Value)}))",
            cancellationToken);
        return new TransactionScope(transaction);
    }

    // Antes UserLifecycleScope: las dos operaciones son una transacción con un lock adentro.
    private sealed class TransactionScope(IDbContextTransaction transaction) : IUserLifecycleScope, ITenantChangeScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta el lock.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "Tenant settings changed while the update was being committed.",
                exception);
        }
        // SDD-CT-06. Registrarse con un slug que alguien ya tomó es un error normal de usuario, no
        // una falla, pero la DbUpdateException cruda no coincide con ninguna rama de
        // ApiExceptionHandler y salía como 500 server.unexpected. Se traduce acá, en Infrastructure,
        // porque es la única capa que puede saber de EF y Npgsql: Modules.Tenancy.Application no
        // referencia ninguno de los dos, y ArchitectureTests lo hace cumplir. La excepción interna
        // se descarta a propósito — un 422 es un resultado esperado, no hay incidente que rastrear.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: TenantSlugIndex,
            })
        {
            throw new TenantDomainException(
                "tenancy.slug.taken",
                "Tenant slug is already in use.");
        }
        // El handler ya pregunta antes con IsAdvisorCodeTakenAsync, pero dos requests pueden
        // pasar ese chequeo a la vez: el índice es la autoridad, y su choque es un 422 del
        // dominio, no un 500.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: AdvisorCodeIndex,
            })
        {
            throw new TenantDomainException(
                "tenancy.membership.advisor_code_taken",
                "The advisor code is already in use in this tenant.");
        }
        // Spec 2026-10-08 §3: dos activaciones simultáneas de una clave sin fila. El candado de la consola
        // lo evita; esto cubre a cualquier otro camino que inserte la misma clave. Sin la traducción, 500.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: TenantModulesPrimaryKey,
            })
        {
            throw new TenantDomainException(
                TenantModuleChangeBatch.NoChangesCode,
                "The module row already exists.");
        }
    }
}
