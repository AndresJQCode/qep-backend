using BuildingBlocks.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record DeleteCompanyCommand(Guid TenantId, Guid CompanyId)
    : ICommand<CompanyDeletedResult>;

// BuildingBlocks no tiene un ICommand sin resultado, y agregárselo sería tocar infraestructura
// compartida por siete módulos para ahorrar un record. Mismo patrón que TaxRateDeletedResult en
// Catalog: el endpoint responde 204 y no lo mira.
public sealed record CompanyDeletedResult(bool Deleted);

/// <summary>
/// Borra una empresa **si nadie la referencia**.
///
/// La condición no la comprueba este handler con una consulta, y no por descuido: hoy ningún
/// módulo del backend referencia una empresa —`Quotes` no existe todavía—, así que no hay
/// repositorio al que preguntarle. Quien impone la regla es PostgreSQL: cualquier clave foránea
/// que llegue a apuntar a `companies.companies(id)` frena el DELETE, y `CompaniesUnitOfWork`
/// traduce esa violación al código `companies.company.in_use` para que el llamador reciba un 422
/// que se entiende en vez de un 500.
///
/// Es la diferencia con `DeleteTaxRateHandler` (CAT-06), que sí consulta antes: ahí el
/// referenciante existe (`Product`) y la consulta previa da el mensaje sin gastar un round trip
/// contra la constraint. Cuando aparezca el primer módulo que apunte a una empresa, la consulta
/// previa se agrega acá y la traducción de abajo queda como red para la carrera — que es el rol
/// que cumple en Catalog.
///
/// Sin validador: el comando no lleva texto libre. Y sin regla de dominio: «estar referenciada»
/// no es un invariante de <see cref="Company"/> —el agregado no sabe quién lo apunta— sino una
/// pregunta sobre el esquema.
///
/// Borrar no exige desactivar primero. Son dos operaciones con propósitos distintos: desactivar
/// conserva la empresa y su historia, borrar es para la que se cargó por error y nadie usó.
/// </summary>
public sealed class DeleteCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteCompanyCommand, CompanyDeletedResult>
{
    public async Task<CompanyDeletedResult> HandleAsync(
        DeleteCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no después: consultar primero le confirma al
        // llamador que el id existe. Mismo orden que el resto del módulo.
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);

        // FindAsync filtra por tenant, así que una empresa ajena sale por acá como 404 y nunca
        // llega al DELETE. Lo que importa no es el status: es que la fila del otro tenant siga
        // existiendo después.
        var company = await repository.FindAsync(
            command.TenantId, new CompanyId(command.CompanyId), cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        var now = clock.UtcNow;
        repository.Remove(company);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.deleted",
            company.Id.ToString(),
            "success",
            now);

        // El evento de auditoría viaja por outbox en este mismo DbContext, así que si el DELETE
        // lo frena una clave foránea el commit falla entero y no queda registrado un borrado que
        // no ocurrió.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CompanyDeletedResult(true);
    }
}
