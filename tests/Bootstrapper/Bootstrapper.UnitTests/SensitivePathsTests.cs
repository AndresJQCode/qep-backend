using Microsoft.AspNetCore.Http;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El token de una invitación viaja en la ruta. <see cref="SensitivePaths.Mask"/> es lo que el log de fallas y su línea de log
/// guardan en su lugar (plan mayorista fase 2, Task 7; rulings O-7 y W2): el segmento que sigue a cada prefijo, en cualquier
/// ruta debajo, sale como <c>***</c>.
/// </summary>
public sealed class SensitivePathsTests
{
    [Theory]
    [InlineData("/api/v1/wholesale-invitations/abc_DEF-1/accept", "/api/v1/wholesale-invitations/***/accept")]
    [InlineData("/api/v1/wholesale-invitations/abc_DEF-1", "/api/v1/wholesale-invitations/***")]
    [InlineData("/api/v1/wholesale-invitations/abc_DEF-1/session-with-password", "/api/v1/wholesale-invitations/***/session-with-password")]
    [InlineData("/api/v1/wholesale-invitations/abc_DEF-1/", "/api/v1/wholesale-invitations/***/")]
    [InlineData("/API/V1/Wholesale-Invitations/abc_DEF-1/accept", "/API/V1/Wholesale-Invitations/***/accept")]
    public void TheWholesaleInvitationTokenIsMasked(string path, string expected) =>
        Assert.Equal(expected, SensitivePaths.Mask(new PathString(path)));

    [Theory]
    [InlineData("/api/v1/invitations/abc_DEF-1", "/api/v1/invitations/***")]
    [InlineData("/api/v1/invitations/abc_DEF-1/accept", "/api/v1/invitations/***/accept")]
    [InlineData("/api/v1/invitations/abc_DEF-1/accept-with-password", "/api/v1/invitations/***/accept-with-password")]
    [InlineData("/api/V1/Invitations/abc_DEF-1/accept", "/api/V1/Invitations/***/accept")]
    public void TheMemberInvitationTokenIsMasked(string path, string expected) =>
        Assert.Equal(expected, SensitivePaths.Mask(new PathString(path)));

    [Theory]
    [InlineData("/api/v1/tenants/0193a3c5-0000-7000-8000-000000000001/wholesale/distributors")]
    [InlineData("/api/v1/tenants/0193a3c5-0000-7000-8000-000000000001/invitations")]
    [InlineData("/api/v1/wholesale-invitations")]
    [InlineData("/api/v1/wholesale-invitations/")]
    [InlineData("/api/v1/wholesale-invitationsx/abc")]
    [InlineData("/")]
    public void OtherPathsAreUntouched(string path) =>
        Assert.Equal(path, SensitivePaths.Mask(new PathString(path)));

    [Fact]
    public void AnEmptyPathIsTheRoot() =>
        Assert.Equal("/", SensitivePaths.Mask(PathString.Empty));
}
