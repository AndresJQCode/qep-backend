using Modules.Audit.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// El camino de auditoría atómica propio de Identity (ADR 0019), acumulado en la unidad de
/// trabajo de <c>IdentityDbContext</c>. Deliberadamente no es el <c>IAuditRecorder</c>
/// compartido entre módulos: esa interfaz se liga una sola vez por DbContext (Tenancy ya la
/// liga a <c>TenancyDbContext</c>), y una segunda ligadura global a <c>IdentityDbContext</c>
/// la taparía en el contenedor de DI — el módulo que registre último se robaría en silencio
/// las escrituras de auditoría de todos los demás hacia el DbContext equivocado, y esas
/// entradas nunca las guardaría la unidad de trabajo del módulo que escribe.
/// </summary>
public interface IIdentityAuditRecorder
{
    void Record(
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human);
}
