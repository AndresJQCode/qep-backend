using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record MembershipListItemDto(
    MembershipId Id,
    Guid UserId,
    string? Email,
    TenantId TenantId,
    MembershipState State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

public static class MembershipListItemMappings
{
    public static MembershipListItemDto ToListItemDto(this Membership membership, string? email) =>
        new(
            membership.Id,
            membership.UserId,
            email,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}

/// <summary>Cuántas membresías caen en cada estado visible, dentro de lo buscado.</summary>
public sealed record MembershipCountsDto(
    int Active,
    int Pending,
    int Expired,
    int Suspended,
    int Removed,
    int Total);

public sealed record MembershipListDto(
    IReadOnlyList<MembershipListItemDto> Items,
    MembershipCountsDto Counts);

/// <summary>
/// Los tres filtros no son intercambiables, y por eso se aplican en este orden.
/// </summary>
/// <remarks>
/// <para><see cref="Role"/> y <see cref="Search"/> acotan el universo: quién entra siquiera
/// en la conversación. El de rol (ej. "advisor") resuelve el selector de asesores de
/// cotizaciones en el servidor, no descartando filas del lado del cliente — facturación
/// nunca puede ser "el asesor" de una cotización.</para>
/// <para><see cref="State"/> es distinto: es la pestaña que se está mirando dentro de ese
/// universo. Por eso los conteos se calculan después de rol y búsqueda pero antes del
/// estado — describen el universo, no la pestaña.</para>
/// </remarks>
public sealed record ListMembershipsQuery(
    TenantId TenantId,
    MembershipViewState? State = null,
    string? Search = null,
    string? Role = null) : IQuery<MembershipListDto>;

public sealed class ListMembershipsHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ListMembershipsQuery, MembershipListDto>
{
    public async Task<MembershipListDto> HandleAsync(
        ListMembershipsQuery query,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(query.TenantId);

        var memberships = await membershipRepository.ListByTenantAsync(
            query.TenantId,
            cancellationToken);

        // Filtrado en memoria, no en el repositorio: mismo criterio que ya documenta este
        // handler para la resolución de email — la cantidad de miembros de un tenant es
        // chica hoy, no justifica un método de repositorio nuevo.
        //
        // El rol se aplica antes de resolver los correos y no después: cada membresía que
        // sobrevive cuesta una consulta a IUserDirectory, así que descartar acá es lo que
        // evita ese trabajo.
        var scoped = query.Role is null
            ? memberships
            : memberships.Where(membership => membership.Roles.Contains(query.Role)).ToList();

        var items = new List<MembershipListItemDto>(scoped.Count);
        // Una búsqueda por membresía: IUserDirectory sólo expone resolución por id único
        // (v1). Aceptable para la poca cantidad de miembros que tiene un tenant hoy.
        foreach (var membership in scoped)
        {
            var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
            items.Add(membership.ToListItemDto(email));
        }

        // Ninguno de los filtros se aplica en SQL, y cada uno por su razón.
        //
        // El correo vive en Identity y llega por IUserDirectory: filtrarlo en la consulta
        // exigiría un join entre módulos, que es lo que ArchitectureTests prohíbe. El
        // estado sí está en la tabla, pero "vencida" no: se deriva comparando ExpiresAt
        // contra el reloj, así que filtrar por la columna cruda daría otra respuesta.
        //
        // Esto no promete rendimiento que no da: la consulta sigue trayendo el roster
        // completo del tenant. Lo que cambia es dónde se decide qué se muestra y con qué
        // reloj — el del servidor, uno solo, en vez del de cada navegador.
        var searched = ApplySearch(items, query.Search);
        var counts = Count(searched);
        var filtered = query.State is null
            ? searched
            : searched.Where(item => ViewStateOf(item) == query.State).ToList();

        return new MembershipListDto(filtered, counts);
    }

    /// <summary>
    /// La búsqueda es por correo y nada más: es el único dato con el que se identifica a una
    /// persona acá, porque Tenancy no guarda nombre y el UserId es un GUID que nadie escribe
    /// de memoria. Una membresía sin correo no coincide con ningún texto.
    /// </summary>
    private static IReadOnlyList<MembershipListItemDto> ApplySearch(
        IReadOnlyList<MembershipListItemDto> items,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return items;
        }

        var term = search.Trim();
        return items
            .Where(item => item.Email is not null &&
                item.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Los conteos se calculan sobre lo buscado pero antes de filtrar por estado: son lo que
    /// decide a cuál de los otros estados vale la pena ir, y contar sólo lo ya filtrado los
    /// dejaría a todos en cero menos uno.
    /// </summary>
    private MembershipCountsDto Count(IReadOnlyList<MembershipListItemDto> items)
    {
        var byState = items
            .GroupBy(ViewStateOf)
            .ToDictionary(group => group.Key, group => group.Count());

        return new MembershipCountsDto(
            byState.GetValueOrDefault(MembershipViewState.Active),
            byState.GetValueOrDefault(MembershipViewState.Pending),
            byState.GetValueOrDefault(MembershipViewState.Expired),
            byState.GetValueOrDefault(MembershipViewState.Suspended),
            byState.GetValueOrDefault(MembershipViewState.Removed),
            items.Count);
    }

    private MembershipViewState ViewStateOf(MembershipListItemDto item) =>
        MembershipViewStates.Of(item.State, item.ExpiresAt, clock.UtcNow);

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read memberships for this tenant.");
        }
    }
}
