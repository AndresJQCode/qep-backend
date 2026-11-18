using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-18: lo que el procesador del aviso por WhatsApp lee y escribe del distribuidor. Un aviso es «vigente» sólo si
/// el distribuidor existe, sigue invitado y su <c>generation</c> es la del evento (D-4): uno viejo no se manda ni pisa el
/// estado. Registrar el resultado no sube <c>Version</c> ni <c>UpdatedAt</c> (D-3) y no guarda: guarda el procesador, con el
/// inbox, en un solo <c>SaveChanges</c>. Interno de Mayorista (D-2): recibe el tenant como todo método de repositorio.
/// </summary>
public sealed class DistributorInvitationNotices(IDistributorRepository distributors)
{
    /// <summary>El distribuidor si el aviso de esa <paramref name="generation"/> sigue vigente; <c>null</c> si no existe, no
    /// está <c>Invited</c> o la generación cambió (un <c>PUT</c> del teléfono o un «Reenviar»).</summary>
    public async Task<Distributor?> FindCurrentAsync(
        Guid tenantId, Guid distributorId, int generation, CancellationToken cancellationToken)
    {
        var distributor = await distributors.GetAsync(tenantId, new DistributorId(distributorId), cancellationToken);
        return distributor is { Status: DistributorStatus.Invited } && distributor.Invitation.Generation == generation
            ? distributor
            : null;
    }

    /// <summary>Aplica el resultado sólo a la generación vigente; <c>false</c> y sin efecto en cualquier otro caso.</summary>
    public async Task<bool> RecordAsync(
        Guid tenantId,
        Guid distributorId,
        int generation,
        InvitationNoticeStatus status,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        var distributor = await FindCurrentAsync(tenantId, distributorId, generation, cancellationToken);
        return distributor is not null && distributor.RecordInvitationNotice(generation, status, failureCode);
    }
}
