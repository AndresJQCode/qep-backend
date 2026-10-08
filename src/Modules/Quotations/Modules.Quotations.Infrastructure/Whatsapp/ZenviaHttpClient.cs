namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// El único <see cref="HttpClient"/> de Zenvia del proceso (spec 2026-10-07): lo comparten el sender
/// de la cuenta de QEP y los que se arman por envío para cada cuenta propia. Uno por envío agotaría
/// sockets. El token viaja por request en <c>X-API-TOKEN</c>, así que compartirlo no mezcla
/// credenciales. Envoltorio y no <see cref="HttpClient"/> suelto para que las pruebas de
/// integración lo reemplacen sin tocar ningún otro cliente HTTP del host.
/// </summary>
internal sealed class ZenviaHttpClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
