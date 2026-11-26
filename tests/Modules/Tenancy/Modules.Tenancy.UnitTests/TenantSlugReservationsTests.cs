using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantSlugReservationsTests
{
    // Las rutas fijas del host del formulario público de Soporte (spec 2026-11-14 §9.2). Un
    // tenant con uno de estos slugs taparía una ruta del host: `soporte.lulocrm.com/consultar`
    // sería a la vez la consulta y el formulario de ese tenant.
    [Fact]
    public void TheListIsTheSupportHostRoutes()
    {
        string[] expected =
        [
            "api", "app", "assets", "static", "public", "consultar", "calificar", "admin", "www",
            "soporte", "support", "pqrsf", "qep", "login", "cuenta", "health", "status", "robots",
            "sitemap",
        ];

        Assert.Equal(19, TenantSlugReservations.Reserved.Count);
        Assert.True(TenantSlugReservations.Reserved.SetEquals(expected));
        foreach (var slug in expected)
        {
            Assert.True(TenantSlugReservations.IsReserved(slug));
        }
    }

    [Fact]
    public void TheComparisonIsOrdinalOverTheNormalizedSlug()
    {
        Assert.False(TenantSlugReservations.IsReserved("Consultar"));
        Assert.False(TenantSlugReservations.IsReserved("consultar-2"));
        Assert.False(TenantSlugReservations.IsReserved("acme"));
    }
}
