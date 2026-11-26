namespace Modules.Store.Application;

/// <summary>
/// Escribe en el outbox de la plataforma dentro de la unidad de trabajo de Store: no guarda, viaja en el <c>SaveChanges</c> de
/// <see cref="IStoreUnitOfWork"/>, así que commitea o revierte con el cambio de negocio.
/// </summary>
public interface IStoreOutbox
{
    void Add(string eventName, object payload, DateTimeOffset occurredAt);
}
