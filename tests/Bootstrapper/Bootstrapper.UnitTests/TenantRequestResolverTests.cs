using Bootstrapper.Authentication;
using Microsoft.AspNetCore.Http;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-14 (Mensajería en tiempo real): el handshake WebSocket del navegador no puede llevar
/// <c>X-Tenant-Id</c>, así que bajo <c>/api/v1/hubs/</c> el tenant también se acepta por el query
/// <c>tenantId</c>. En cualquier otra ruta el query no cuenta: sería una segunda puerta que nadie pidió.
/// La membresía se valida igual después, venga de donde venga.
/// </summary>
public sealed class TenantRequestResolverTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Other = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    private static HttpRequest Request(string path, string? header = null, string? query = null, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (header is not null)
        {
            context.Request.Headers["X-Tenant-Id"] = header;
        }

        if (query is not null)
        {
            context.Request.QueryString = new QueryString($"?tenantId={query}");
        }

        return context.Request;
    }

    [Fact]
    public void TheHeaderResolvesOnAnyRoute()
    {
        Assert.True(TenantRequestResolver.TryResolve(Request("/api/v1/tenants/x/messaging/conversations", header: Tenant.ToString()), out var resolved));
        Assert.Equal(Tenant, resolved);
    }

    [Fact]
    public void TheHeaderWinsOverTheQueryUnderTheHub()
    {
        Assert.True(TenantRequestResolver.TryResolve(Request("/api/v1/hubs/messaging", header: Tenant.ToString(), query: Other.ToString()), out var resolved));
        Assert.Equal(Tenant, resolved);
    }

    [Theory]
    [InlineData("/api/v1/hubs/messaging")]
    [InlineData("/api/v1/hubs/messaging/negotiate")]
    public void TheQueryResolvesOnlyUnderTheHubPrefix(string path)
    {
        Assert.True(TenantRequestResolver.TryResolve(Request(path, query: Tenant.ToString()), out var resolved));
        Assert.Equal(Tenant, resolved);
    }

    [Theory]
    [InlineData("/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/conversations")]
    [InlineData("/api/v1/auth/preferences")]
    [InlineData("/api/v1/hubsmessaging")]
    public void TheQueryIsIgnoredAnywhereElse(string path)
    {
        Assert.False(TenantRequestResolver.TryResolve(Request(path, query: Tenant.ToString()), out var resolved));
        Assert.Equal(Guid.Empty, resolved);
    }

    // Bug 4 de PENDIENTES (2026-10-15): el medio lo pide un <audio>/<img>/<video>, que no puede llevar
    // X-Tenant-Id. Sin tenant no hay permisos y la API contestaba 403 a una sesión válida.
    [Fact]
    public void ThePathResolvesOnTheMediaRoute()
    {
        Assert.True(TenantRequestResolver.TryResolve(Request($"/api/v1/tenants/{Tenant}/messaging/media/{Other}"), out var resolved));
        Assert.Equal(Tenant, resolved);
    }

    [Fact]
    public void TheHeaderWinsOverThePathOnTheMediaRoute()
    {
        Assert.True(TenantRequestResolver.TryResolve(Request($"/api/v1/tenants/{Other}/messaging/media/{Other}", header: Tenant.ToString()), out var resolved));
        Assert.Equal(Tenant, resolved);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/conversations")]
    [InlineData("GET", "/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/media")]
    [InlineData("GET", "/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/media/not-a-guid")]
    [InlineData("GET", "/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/media/01900000-0000-7000-8000-0000000000b2/x")]
    [InlineData("GET", "/api/v1/tenants/not-a-guid/messaging/media/01900000-0000-7000-8000-0000000000b2")]
    [InlineData("POST", "/api/v1/tenants/01900000-0000-7000-8000-0000000000b1/messaging/media/01900000-0000-7000-8000-0000000000b2")]
    public void ThePathIsIgnoredAnywhereElse(string method, string path)
    {
        Assert.False(TenantRequestResolver.TryResolve(Request(path, method: method), out var resolved));
        Assert.Equal(Guid.Empty, resolved);
    }

    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData(null, "not-a-guid")]
    [InlineData("", "")]
    public void AValueThatIsNotAGuidDoesNotResolve(string? header, string? query)
    {
        Assert.False(TenantRequestResolver.TryResolve(Request("/api/v1/hubs/messaging", header, query), out _));
    }

    [Fact]
    public void ANullRequestDoesNotResolve()
    {
        Assert.False(TenantRequestResolver.TryResolve(null, out _));
    }
}
