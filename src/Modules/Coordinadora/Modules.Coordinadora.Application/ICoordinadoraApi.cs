namespace Modules.Coordinadora.Application;

/// <summary>
/// Las credenciales de una conexión de Coordinadora, ya descifradas por el puerto de conexiones (spec 2026-11-21 §3): las del AGS
/// (<see cref="ApiKey"/>, <see cref="ApiPassword"/>, más el <see cref="Nit"/>) y las del servidor de guías (<see cref="ClientId"/>,
/// <see cref="GuidesUser"/>, <see cref="GuidesPassword"/>; la clave viaja como su SHA-256). Nunca se imprimen: el <c>ToString</c> no las
/// escribe (un <c>record</c> las escribiría), así que un log o una excepción con el objeto no las filtran.
/// </summary>
public sealed record CoordinadoraCredentials(
    Guid ConnectionId,
    string Nit,
    string ApiKey,
    string ApiPassword,
    string ClientId,
    string GuidesUser,
    string GuidesPassword)
{
    public override string ToString() => $"CoordinadoraCredentials {{ ConnectionId = {ConnectionId} }}";
}

/// <summary>
/// El cuerpo <c>p</c> de <c>Cotizador.cotizar</c> ya serializado y <b>sin</b> credenciales (el cliente agrega <c>nit</c>, <c>apikey</c> y
/// <c>clave</c> al mandar). Es texto a propósito: es lo que la vista previa muestra y lo que el armador probó.
/// </summary>
public sealed record CoordinadoraQuoteRequest(string Json)
{
    public override string ToString() => "CoordinadoraQuoteRequest { }";
}

/// <summary>El cuerpo <c>p</c> de <c>Guias_generarGuia</c>, ya serializado y sin credenciales (el cliente agrega <c>id_cliente</c>, <c>usuario</c> y
/// <c>clave</c>). Lleva datos del destinatario: nunca sale a un log.</summary>
public sealed record CoordinadoraGuideRequest(string Json)
{
    public override string ToString() => "CoordinadoraGuideRequest { }";
}

/// <summary>El resultado de <c>Cotizador.cotizar</c>. Una unión cerrada: <see cref="Quoted"/>, <see cref="Rejected"/> o <see cref="Transient"/>.</summary>
public abstract record CoordinadoraQuoteOutcome
{
    private CoordinadoraQuoteOutcome()
    {
    }

    /// <summary>Coordinadora cotizó. <paramref name="ResultJson"/> es el <c>result</c> tal cual (V-4: su forma no está documentada) y
    /// <paramref name="FreightTotal"/> lo que se pudo leer de él (<c>flete_total</c> o <c>total</c>).</summary>
    public sealed record Quoted(string ResultJson, decimal? FreightTotal) : CoordinadoraQuoteOutcome;

    /// <summary>Coordinadora respondió un error (JSON-RPC <c>error</c> o un <c>result</c> con error): credenciales, ciudad, cuenta…</summary>
    public sealed record Rejected(string Code, string Message) : CoordinadoraQuoteOutcome;

    /// <summary>5xx, timeout, red o una respuesta ilegible.</summary>
    public sealed record Transient(string Reason) : CoordinadoraQuoteOutcome;
}

/// <summary>El resultado de <c>Guias_generarGuia</c>.</summary>
public abstract record CoordinadoraGuideOutcome
{
    private CoordinadoraGuideOutcome()
    {
    }

    /// <summary>Coordinadora creó la guía. <paramref name="LabelPdf"/> sólo viene si la respuesta trajo el PDF (V-2).</summary>
    public sealed record Created(string GuideNumber, byte[]? LabelPdf) : CoordinadoraGuideOutcome;

    /// <summary>Coordinadora la rechazó (SOAP Fault o un resultado con error): no la creó.</summary>
    public sealed record Rejected(string Code, string Message) : CoordinadoraGuideOutcome;

    /// <summary>5xx, timeout, red o una respuesta ilegible: no se sabe si la creó.</summary>
    public sealed record Transient(string Reason) : CoordinadoraGuideOutcome;
}

/// <summary>El resultado de <c>Guias_anularGuia</c>.</summary>
public abstract record CoordinadoraCancelOutcome
{
    private CoordinadoraCancelOutcome()
    {
    }

    public sealed record Cancelled : CoordinadoraCancelOutcome;

    public sealed record Rejected(string Code, string Message) : CoordinadoraCancelOutcome;

    public sealed record Transient(string Reason) : CoordinadoraCancelOutcome;
}

/// <summary>Lo que dio <c>Guias_rastreoSimple</c> (V-5): el estado y su descripción si vienen con ese nombre, y si no el texto del resultado.</summary>
public sealed record CoordinadoraTracking(string? Status, string? Detail);

/// <summary>
/// El puerto de la API de Coordinadora (spec 2026-11-21 §3). Todas las llamadas reciben las credenciales de la conexión. Las lecturas
/// (rótulo, rastreo) lanzan: <c>ServiceUnavailableException</c> <c>coordinadora.unavailable</c> si Coordinadora no contestó o dio un error
/// transitorio, y <c>CoordinadoraDomainException</c> <c>coordinadora.carrier_rejected</c> si respondió un error propio. Cotizar, generar y
/// anular no lanzan por eso: devuelven su resultado, porque «no sé si se creó» es un resultado que el flujo tiene que guardar. Sin
/// reintentos propios y <b>sin reenvío</b> de una guía: no hay clave de idempotencia.
/// </summary>
public interface ICoordinadoraApi
{
    /// <summary><c>Cotizador.cotizar</c> por JSON-RPC al AGS, con <c>nit</c>, <c>apikey</c> y <c>clave</c> dentro de <c>p</c>.</summary>
    Task<CoordinadoraQuoteOutcome> QuoteAsync(
        CoordinadoraCredentials credentials, CoordinadoraQuoteRequest request, CancellationToken cancellationToken);

    /// <summary><c>Guias_generarGuia</c> por SOAP 1.2 al servidor de guías, con <c>id_cliente</c>, <c>usuario</c> y <c>clave</c> (SHA-256) dentro de <c>p</c>.</summary>
    Task<CoordinadoraGuideOutcome> CreateGuideAsync(
        CoordinadoraCredentials credentials, CoordinadoraGuideRequest request, CancellationToken cancellationToken);

    /// <summary><c>Guias_imprimirRotulos</c>: el PDF del rótulo, ya decodificado. Nunca se cachea.</summary>
    Task<byte[]> PrintLabelAsync(CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken);

    /// <summary><c>Guias_rastreoSimple</c>: el estado de la guía.</summary>
    Task<CoordinadoraTracking> TrackAsync(CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken);

    /// <summary><c>Guias_anularGuia</c>.</summary>
    Task<CoordinadoraCancelOutcome> CancelGuideAsync(
        CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken);
}
