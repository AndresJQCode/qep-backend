using Modules.Platform.Domain;

namespace Modules.Platform.UnitTests;

/// <summary>
/// El log de la aplicación, del lado del dominio.
///
/// Lo que más se prueba acá es <see cref="RequestFailure.ModuleFor"/>: es lo que llena la columna
/// por la que se filtra, y se calcula una sola vez —al escribir— así que una fila mal clasificada
/// queda mal para siempre.
/// </summary>
public sealed class RequestFailureTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private const string TenantSegment = "01900000-0000-7000-8000-000000000001";

    [Theory]
    // Lo que vive dentro de un espacio de trabajo: el tenant se saltea, porque como módulo no
    // distinguiría nada — es el mismo para todas las filas del reporte.
    [InlineData($"/api/v1/tenants/{TenantSegment}/catalog/products", "catalog")]
    [InlineData($"/api/v1/tenants/{TenantSegment}/quotations/abc/send", "quotations")]
    [InlineData($"/api/v1/tenants/{TenantSegment}/reports/sales", "reports")]
    [InlineData($"/api/v1/tenants/{TenantSegment}/platform/request-log", "platform")]
    // Lo que no vive dentro de un tenant.
    [InlineData("/api/v1/auth/session", "auth")]
    [InlineData("/api/v1/roles", "roles")]
    // Una ruta que opera sobre el tenant mismo, sin segmento de módulo.
    [InlineData($"/api/v1/tenants/{TenantSegment}", "tenants")]
    [InlineData("/api/v1/tenants", "tenants")]
    public void ModuleForReadsTheModuleOutOfThePath(string path, string expected)
    {
        Assert.Equal(expected, RequestFailure.ModuleFor(path));
    }

    // La query string no es parte de la ruta, y la barra final tampoco: sin esto, la misma ruta
    // se clasificaria distinto segun como la haya escrito el cliente.
    [Theory]
    [InlineData($"/api/v1/tenants/{TenantSegment}/catalog/products?page=2&pageSize=50")]
    [InlineData($"/api/v1/tenants/{TenantSegment}/catalog/products/")]
    [InlineData($"/api/v1/tenants/{TenantSegment}/CATALOG/products")]
    public void ModuleForIgnoresTheQueryStringTheTrailingSlashAndTheCase(string path)
    {
        Assert.Equal("catalog", RequestFailure.ModuleFor(path));
    }

    // Una ruta que no reconoce igual deja fila: tiene el error, la ruta y la traza, que es lo que
    // se vino a buscar. Perderla por no poder clasificarla seria el peor canje posible.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/health/live")]
    [InlineData("/api")]
    [InlineData("/openapi/v1.json")]
    public void ModuleForFallsBackWhenThePathIsNotAnApiRoute(string? path)
    {
        Assert.Equal(RequestFailure.UnknownModule, RequestFailure.ModuleFor(path));
    }

    /// <summary>
    /// El tenant sale de la ruta, y esta prueba existe por un defecto real: salia de los route
    /// values de ASP.NET, que <c>ExceptionHandlerMiddleware</c> limpia antes de invocar al
    /// manejador. Toda falla con excepcion quedaba con el tenant nulo, y el reporte --que filtra
    /// por tenant-- no podia mostrar ninguna.
    /// </summary>
    [Fact]
    public void CreateResolvesTheTenantFromThePath()
    {
        var failure = NewFailure(
            path: $"/api/v1/tenants/{TenantSegment}/quotations/abc/send");

        Assert.Equal(Guid.Parse(TenantSegment), failure.TenantId);
    }

    // Las rutas que no viven dentro de un tenant quedan sin el, y eso es correcto: no pertenecen
    // al espacio de trabajo de nadie.
    [Theory]
    [InlineData("/api/v1/auth/session")]
    [InlineData("/api/v1/tenants")]
    [InlineData("/api/v1/tenants/no-es-un-guid/catalog/products")]
    [InlineData("/health/live")]
    public void TenantIdForIsNullOutsideATenantRoute(string path)
    {
        Assert.Null(RequestFailure.TenantIdFor(path));
    }

    [Fact]
    public void TenantIdForIgnoresTheQueryString()
    {
        Assert.Equal(
            Guid.Parse(TenantSegment),
            RequestFailure.TenantIdFor(
                $"/api/v1/tenants/{TenantSegment}/platform/request-log?page=2"));
    }

    [Fact]
    public void CreateResolvesTheModuleFromThePath()
    {
        var failure = NewFailure(path: $"/api/v1/tenants/{TenantSegment}/customers/import");

        Assert.Equal("customers", failure.Module);
    }

    // La columna no admite null y una excepcion sin mensaje es rara pero posible: el registro vale
    // igual por su traza y su codigo, asi que no se pierde por eso.
    [Fact]
    public void CreateKeepsARowEvenWithoutMessageOrDetail()
    {
        var failure = NewFailure(message: "   ", detail: "");

        Assert.Equal("(sin mensaje)", failure.Message);
        Assert.Equal("(sin detalle)", failure.Detail);
    }

    // Recortar aca y no en PostgreSQL: la base respondería con un error en vez de guardar la
    // fila, y perder el registro de la falla por culpa del tamaño de la falla seria absurdo.
    [Fact]
    public void CreateTruncatesADetailLongerThanTheColumn()
    {
        var detail = new string('x', RequestFailure.DetailMaxLength + 500);

        var failure = NewFailure(detail: detail);

        Assert.Equal(RequestFailure.DetailMaxLength, failure.Detail.Length);
    }

    [Fact]
    public void CreateTruncatesAMessageLongerThanTheColumn()
    {
        var message = new string('x', RequestFailure.MessageMaxLength + 50);

        var failure = NewFailure(message: message);

        Assert.Equal(RequestFailure.MessageMaxLength, failure.Message.Length);
    }

    // Un codigo de error vacio se guarda como null y no como cadena vacia: null significa "no
    // habia codigo", y es lo que la pantalla muestra como falla no prevista.
    [Fact]
    public void CreateNormalizesAnEmptyErrorCodeToNull()
    {
        var failure = NewFailure(errorCode: "  ");

        Assert.Null(failure.ErrorCode);
    }

    private static RequestFailure NewFailure(
        string path = "/api/v1/auth/session",
        string? errorCode = "server.unexpected",
        string message = "Boom",
        string detail = "System.Exception: Boom") =>
        RequestFailure.Create(
            RequestFailureId.New(),
            subjectId: null,
            method: "POST",
            path: path,
            statusCode: 500,
            errorCode: errorCode,
            message: message,
            detail: detail,
            traceId: "trace-1",
            occurredAt: Now);
}
