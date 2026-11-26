using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// El libro es historia inmutable: el usuario no se borra mientras algo suyo siga nombrado. Dos ids distintos, a propósito:
/// <c>stock_movements.actor_id</c> y las columnas de las tareas (<c>tasks.created_by/assigned_member_id/claimed_by_member_id</c> y
/// <c>task_events.member_id</c>) guardan la <b>membresía</b> (<c>WmsCaller.RequireAsync</c>) y
/// <c>adjustments.proposed_by/approved_by/rejected_by</c> guardan el <b>usuario</b> (<c>SubjectId</c>). Por eso la sonda
/// resuelve las membresías del usuario, como <c>PosUserReferenceProbe</c>, y compara cada columna con su id.
/// </summary>
/// <remarks>
/// Costo: <c>stock_movements</c> está particionada por mes y <c>actor_id</c> no tiene índice (no se agrega uno a una tabla
/// particionada de crecimiento ilimitado), así que el <c>EXISTS</c> recorre las particiones hasta el primer acierto y, si el
/// usuario no tiene movimientos, todas. Las columnas de tareas tampoco tienen un índice que sirva para esta pregunta. La corre el
/// worker de huérfanos, un usuario a la vez y fuera del camino de una petición.
/// </remarks>
internal sealed class WmsUserReferenceProbe(
    WmsDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "wms";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = (await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken)).ToArray();

        return (await dbContext.Database.SqlQuery<bool>($"""
            SELECT (
                EXISTS (SELECT 1 FROM wms.stock_movements WHERE actor_id = ANY({membershipIds}))
                OR EXISTS (
                    SELECT 1 FROM wms.tasks
                    WHERE created_by = ANY({membershipIds}) OR assigned_member_id = ANY({membershipIds})
                       OR claimed_by_member_id = ANY({membershipIds}))
                OR EXISTS (SELECT 1 FROM wms.task_events WHERE member_id = ANY({membershipIds}))
                OR EXISTS (
                    SELECT 1 FROM wms.adjustments
                    WHERE proposed_by = {userId} OR approved_by = {userId} OR rejected_by = {userId})) AS "Value"
            """).ToListAsync(cancellationToken)).Single();
    }
}
