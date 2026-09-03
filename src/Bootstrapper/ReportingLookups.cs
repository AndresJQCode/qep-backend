using Microsoft.EntityFrameworkCore;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Resuelve la etiqueta con la que mostrar a una persona en un reporte.
///
/// **El sistema no guarda nombre de persona en ningun lado.**
/// <c>Modules.Identity.Domain.User</c> tiene <c>Email</c>, estado y marcas de tiempo;
/// <c>Modules.Tenancy.Domain.Membership</c> tiene <c>UserId</c> y estado, y ni eso. Asi que el
/// email es el unico identificador legible que existe, y es lo que viaja en <c>advisorName</c> y
/// <c>changedByName</c> — los nombres de campo se mantienen porque son los que fija el contrato
/// de API con el frontend.
///
/// Vive en <c>Bootstrapper</c> y no en un modulo porque toca los DbContext de **dos** modulos a
/// la vez (Tenancy e Identity), que es exactamente el acoplamiento que solo el composition root
/// puede tener.
///
/// **Siempre en lote, nunca fila por fila.** Una consulta por fila de reporte es N+1 sobre una
/// pagina de hasta 200 filas y sobre una exportacion de hasta 50_000.
/// </summary>
internal sealed class ReportingPeopleLookup(
    TenancyDbContext tenancy,
    IdentityDbContext identity)
{
    /// <summary>
    /// Email por <b>id de membresia</b>, que es lo que guarda <c>Quotation.AdvisorId</c>. La
    /// cadena es <c>MembershipId</c> → <c>Membership.UserId</c> → <c>User.Email</c>.
    ///
    /// Una membresia sin fila de usuario simplemente no aparece en el diccionario, y el reporte
    /// muestra <c>null</c>: un reporte que se cae porque a una persona le falta la fila de
    /// usuario es peor que uno con una etiqueta vacia.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> EmailsByMembershipIdAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        if (membershipIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = membershipIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var memberships = await tenancy.Memberships
            .AsNoTracking()
            .Where(membership => ids.Contains(membership.Id))
            .Select(membership => new { membership.Id, membership.UserId })
            .ToListAsync(cancellationToken);
        if (memberships.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var emailsByUserId = await EmailsByUserIdAsync(
            memberships.Select(membership => membership.UserId).ToArray(), cancellationToken);

        var result = new Dictionary<Guid, string>(memberships.Count);
        foreach (var membership in memberships)
        {
            if (emailsByUserId.TryGetValue(membership.UserId, out var email))
            {
                result[membership.Id.Value] = email;
            }
        }

        return result;
    }

    /// <summary>
    /// Email por <b>id de usuario</b>, que es lo que guarda <c>ProductPriceChange.ChangedBy</c>:
    /// ahi el autor es el subject de la ejecucion (<c>IExecutionContext.SubjectId</c>), o sea el
    /// id interno de <c>identity.users</c>, sin pasar por ninguna membresia.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> EmailsByUserIdAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().Select(id => new UserId(id)).ToArray();
        var users = await identity.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Email })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id.Value, user => user.Email);
    }
}

/// <summary>
/// Resuelve nombre y CUC de los clientes de una pagina de reporte.
///
/// Vive aca por lo mismo que <c>QuotationCustomerLookup</c>: los reportes de ventas y de
/// cotizaciones necesitan un dato de <c>customers</c>, y ningun modulo de negocio referencia a
/// otro. En lote, por lo mismo que <see cref="ReportingPeopleLookup"/>.
/// </summary>
internal sealed class ReportingClientLookup(CustomersDbContext customers)
{
    public async Task<IReadOnlyDictionary<Guid, ReportingClientRef>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken cancellationToken)
    {
        if (clientIds.Count == 0)
        {
            return new Dictionary<Guid, ReportingClientRef>();
        }

        var ids = clientIds.Distinct().Select(id => new CustomerId(id)).ToArray();
        var rows = await customers.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && ids.Contains(customer.Id))
            .Select(customer => new { customer.Id, customer.Name, customer.Cuc })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id.Value,
            row => new ReportingClientRef(row.Name, row.Cuc));
    }
}

/// <summary>Lo unico que los reportes de ventas y cotizaciones necesitan de un cliente.</summary>
internal sealed record ReportingClientRef(string Name, string Cuc);

/// <summary>
/// Traduce un rango de fechas calendarias al rango de instantes con el que se consulta.
///
/// El limite superior es **exclusivo al dia siguiente** y no <c>&lt;=</c> sobre el mismo dia: el
/// contrato dice "inclusive (whole day)", y un <c>&lt;= to</c> contra una columna
/// <c>timestamptz</c> deja afuera todo lo que paso despues de la medianoche del ultimo dia.
///
/// En UTC, que es como estan guardadas las columnas. Un tenant en America/Bogota vera el corte
/// del dia en UTC y no en su huso; alinearlo al huso del tenant es una decision de producto que
/// el contrato no toma, asi que no se inventa aca.
/// </summary>
internal static class ReportDateRange
{
    public static DateTimeOffset InclusiveStart(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateTimeOffset ExclusiveEnd(DateOnly date) =>
        new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
