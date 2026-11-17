using Bootstrapper.Cors;
using Microsoft.AspNetCore.Http;
using Modules.Support.Api;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El prefijo del formulario público de Soporte vive dos veces (plan fase 3, F5): en Bootstrapper, para la excepción de CSRF, el
/// <c>no-store</c> y el log de fallas, y en <c>PublicSupportEndpoints</c>, que es el grupo de verdad. Bootstrapper no referencia los
/// <c>.Api</c>, así que esta prueba —que sí ve los dos— es la que impide que se separen. Lo mismo con el nombre de la política CORS.
/// </summary>
public sealed class SupportPublicPathsTests
{
    [Fact]
    public void ThePrefixMatchesTheEndpointGroup()
    {
        Assert.Equal(SupportPublicPaths.Prefix + "{tenantSlug}", PublicSupportEndpoints.GroupPattern);
        Assert.True(SupportPublicPaths.IsSupportPublic(new PathString("/api/v1/public/support/mi-empresa/form")));
    }

    [Fact]
    public void TheCorsPolicyNameMatchesTheEndpointGroup() =>
        Assert.Equal(CorsServiceCollectionExtensions.SupportPublicPolicyName, PublicSupportEndpoints.CorsPolicyName);

    [Theory]
    [InlineData("/api/v1/public/support")]
    [InlineData("/api/v1/public/supportx/a")]
    [InlineData("/API/v1/public/support/a/form")]
    [InlineData("/api/v1/tenants/x/support/tickets")]
    public void ANearMissIsNotThePublicGroup(string path) =>
        Assert.False(SupportPublicPaths.IsSupportPublic(new PathString(path)));
}
