namespace Modules.Siigo.Application;

/// <summary>
/// Una llamada a Siigo que falló, lista para el log de fallas de la aplicación (la pantalla <c>platform/request-log</c>).
/// <paramref name="Diagnostic"/> es el <c>curl</c> reproducible de la petición (token y <c>access_key</c> enmascarados) y la respuesta de
/// Siigo; lleva el cuerpo de la factura o del cliente, es decir datos personales, y por eso sólo viaja a ese log, que lee el operador.
/// </summary>
public sealed record SiigoCallFailure(int? Status, string Reason, string Diagnostic)
{
    public override string ToString() => "SiigoCallFailure { }";
}

/// <summary>
/// Puerto hacia el log de fallas de la aplicación. Lo llama el cliente de Siigo cuando la llamada de una factura falla <b>sin lanzar</b>
/// (rechazo o fallo transitorio): una excepción ya llega a ese log por el manejador de la API, con el curl en <c>Exception.Data</c>.
/// No propaga sus propios errores: quien lo llama está a punto de devolver el resultado real.
/// </summary>
public interface ISiigoCallFailureLog
{
    Task RecordAsync(SiigoCallFailure failure, CancellationToken cancellationToken);
}
