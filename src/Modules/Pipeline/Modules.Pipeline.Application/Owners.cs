using System.Globalization;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>
/// BFF (D21 del spec del frontend, H43): el formulario de negocio sólo DIBUJA esta lista. Quién puede ser dueño lo decide el
/// backend —una membresía <c>Active</c> del tenant, la misma regla que valida <c>pipeline.deal.owner_not_member</c>—, no la
/// pantalla. Sin este endpoint el selector dependería de <c>GET /memberships</c>, que exige <c>advisorship.read</c> (un permiso de
/// Tenancy ajeno al embudo) y tendría que repetir la regla de la membresía activa. El nombre viaja resuelto
/// (<c>DisplayName</c> o, si no tiene, el correo): la pantalla nunca recibe <c>null</c> ni vacío, que dejaría una opción sin
/// texto.
/// </summary>
public sealed record DealOwnerDto(Guid MemberId, string Name)
{
    /// <summary>Un miembro activo sin nombre ni correo resoluble sigue siendo un dueño válido (<c>AnActiveMemberWithoutNameOrEmailCanOwnADeal</c>):
    /// se lista con este texto en vez de callarlo. Es un caso defensivo; todo usuario tiene correo.</summary>
    public const string UnnamedMemberName = "Miembro sin nombre";
}

public sealed record GetDealOwnersQuery(Guid TenantId) : IQuery<IReadOnlyList<DealOwnerDto>>;

/// <summary>D21 y D22: <c>pipeline.deal.manage</c>, el único permiso que revisa el backend para asignar un negocio (no hay regla
/// de sólo administrador). Las membresías activas salen de <see cref="IPipelineActiveMembers"/>, los nombres de
/// <see cref="IPipelineMemberNames"/>; el orden es por nombre con la cultura <c>es-CO</c> (el acento de <c>Ángela</c> no la manda
/// después de <c>Zoe</c>) y, de desempate, por id.</summary>
public sealed class GetDealOwnersHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineActiveMembers activeMembers,
    IPipelineMemberNames memberNames)
    : IQueryHandler<GetDealOwnersQuery, IReadOnlyList<DealOwnerDto>>
{
    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("es-CO"), ignoreCase: true);

    public async Task<IReadOnlyList<DealOwnerDto>> HandleAsync(GetDealOwnersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealManage, cancellationToken);

        var ids = await activeMembers.ListActiveAsync(query.TenantId, cancellationToken);
        var names = await memberNames.FindAsync(query.TenantId, ids, cancellationToken);

        return
        [
            .. ids
                .Select(id => new DealOwnerDto(id, names.GetValueOrDefault(id) ?? DealOwnerDto.UnnamedMemberName))
                .OrderBy(owner => owner.Name, ByName)
                .ThenBy(owner => owner.MemberId),
        ];
    }
}
