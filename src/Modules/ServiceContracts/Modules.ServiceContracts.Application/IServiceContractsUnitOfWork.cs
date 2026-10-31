namespace Modules.ServiceContracts.Application;

public interface IServiceContractsUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Suelta todo lo que el contexto sigue: la importación guarda fila por fila, y una fila que falló no puede arrastrar sus entidades a la siguiente.</summary>
    void Clear();
}
