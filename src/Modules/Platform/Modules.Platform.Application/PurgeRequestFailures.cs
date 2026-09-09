using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Platform.Application;

/// <summary>
/// Borra del log lo que ya envejeció: todo lo anterior a <see cref="RetentionDays"/> días.
///
/// Existe porque el log guarda **toda** falla, incluidas las de validación de formulario, que son
/// las más frecuentes de lejos. Sin una forma de vaciarlo, la tabla crece sin techo y la pantalla
/// que sirve para encontrar un incidente termina enterrándolo bajo miles de filas de gente que
/// dejó un campo vacío.
///
/// La ventana es fija y no un parámetro del cliente a propósito: un endpoint que acepta "borrá
/// todo lo anterior a esta fecha" acepta también la fecha de mañana, y eso es un borrado total
/// disfrazado de mantenimiento. Una semana es lo que queda: alcanza para diagnosticar algo que
/// pasó el fin de semana y no alcanza para acumular.
/// </summary>
public sealed record PurgeRequestFailuresCommand(Guid TenantId)
    : ICommand<PurgeRequestFailuresResult>;

/// <param name="DeletedCount">Cuántas filas se borraron, para poder decirlo en la pantalla: un
/// "listo" sin número no distingue "borré 4.000" de "no había nada".</param>
/// <param name="OlderThan">El corte que se aplicó. Viaja de vuelta para que la pantalla no tenga
/// que recalcularlo y arriesgarse a mostrar una fecha distinta de la que se usó.</param>
public sealed record PurgeRequestFailuresResult(int DeletedCount, DateTimeOffset OlderThan);

public sealed class PurgeRequestFailuresHandler(
    IRequestFailureRepository repository,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<PurgeRequestFailuresCommand, PurgeRequestFailuresResult>
{
    /// <summary>Lo que se conserva: la última semana.</summary>
    public const int RetentionDays = 7;

    public async Task<PurgeRequestFailuresResult> HandleAsync(
        PurgeRequestFailuresCommand command,
        CancellationToken cancellationToken)
    {
        PlatformAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PlatformPermissions.RequestLogPurge);

        var olderThan = clock.UtcNow.AddDays(-RetentionDays);
        var deleted = await repository.PurgeOlderThanAsync(
            command.TenantId, olderThan, cancellationToken);

        return new PurgeRequestFailuresResult(deleted, olderThan);
    }
}
