using System.Globalization;
using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.UnitTests;

public sealed class ApiKeyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();

    private static ApiKey NewKey(
        string name = "Integración ERP",
        string[]? scopes = null,
        Guid[]? connections = null,
        DateTimeOffset? expiresAt = null) =>
        ApiKey.Create(
            Tenant,
            name,
            ApiKeyMaterial.Generate(),
            scopes ?? ["whatsapp.templates.send"],
            connections,
            expiresAt,
            Member,
            Now);

    [Theory]
    [InlineData(null, false, ApiKeyStatus.Active)]
    [InlineData("2026-11-29T12:00:00Z", false, ApiKeyStatus.ExpiringSoon)] // justo 30 días
    [InlineData("2026-11-29T12:00:01Z", false, ApiKeyStatus.Active)]
    [InlineData("2026-10-30T12:00:00Z", false, ApiKeyStatus.Expired)] // vence en este instante
    [InlineData("2026-10-01T00:00:00Z", true, ApiKeyStatus.Revoked)] // revocada gana a vencida
    public void StatusFollowsTheSpecTable(string? expiresAt, bool revoked, ApiKeyStatus expected)
    {
        var key = NewKey(expiresAt: expiresAt is null ? null : DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture));
        if (revoked)
        {
            key.Revoke(Member, Now);
        }

        Assert.Equal(expected, key.StatusAt(Now));
    }

    [Fact]
    public void ARevokedOrExpiredKeyIsNotUsable()
    {
        var expired = NewKey(expiresAt: Now);
        var revoked = NewKey();
        revoked.Revoke(Member, Now);

        Assert.False(expired.IsUsableAt(Now));
        Assert.False(revoked.IsUsableAt(Now));
        Assert.True(NewKey().IsUsableAt(Now));
    }

    [Fact]
    public void CreateKeepsThePrefixAndHashButNeverThePlaintext()
    {
        var material = ApiKeyMaterial.Generate();

        var key = ApiKey.Create(Tenant, "  ERP  ", material, ["a"], null, null, Member, Now);

        Assert.Equal("ERP", key.Name);
        Assert.Equal(material.Prefix, key.KeyPrefix);
        Assert.Equal(material.SecretHash, key.SecretHash);
        Assert.Null(key.ConnectionIds);
    }

    [Fact]
    public void CreateRejectsAnEmptyConnectionList() =>
        Assert.Throws<ApiAccessDomainException>(() => NewKey(connections: []));

    [Fact]
    public void CreateRejectsAnEmptyScopeList() =>
        Assert.Throws<ApiAccessDomainException>(() => NewKey(scopes: []));

    [Fact]
    public void CreateRejectsANameLongerThan80() =>
        Assert.Throws<ApiAccessDomainException>(() => NewKey(name: new string('a', ApiKey.NameMaxLength + 1)));

    [Fact]
    public void RevokingTwiceKeepsTheFirstRevocation()
    {
        var key = NewKey();
        var first = Guid.NewGuid();

        Assert.True(key.Revoke(first, Now));
        var version = key.Version;

        Assert.False(key.Revoke(Guid.NewGuid(), Now.AddDays(1)));
        Assert.Equal(Now, key.RevokedAt);
        Assert.Equal(first, key.RevokedByMemberId);
        Assert.Equal(version, key.Version);
    }
}
