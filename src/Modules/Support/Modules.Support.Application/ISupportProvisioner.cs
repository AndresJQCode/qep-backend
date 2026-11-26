namespace Modules.Support.Application;

/// <summary>Plan D1: Tenancy no avisa cuando se prende un módulo, así que la fila de soporte del tenant y sus ocho tipos se
/// siembran en el primer acceso. Idempotente y segura ante dos primeros accesos a la vez.
/// <para>Abre y confirma su propia transacción sobre el contexto del módulo: se llama <b>antes</b> de
/// <see cref="ISupportUnitOfWork.BeginTransactionAsync"/>, nunca dentro. Dentro de una transacción abierta, EF no deja abrir otra y
/// el alta fallaría.</para></summary>
public interface ISupportProvisioner
{
    Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-22: siembra el tipo interno de un tenant que ya estaba aprovisionado (la migración lo hace para los de
    /// hoy; esto cubre al que se aprovisionó entre tanto). <c>INSERT … ON CONFLICT DO NOTHING</c>, idempotente, con su propia
    /// transacción como <see cref="EnsureAsync"/>: se llama antes de abrir una unidad de trabajo, nunca dentro.</summary>
    Task EnsureInternalTypeAsync(Guid tenantId, CancellationToken cancellationToken);
}
