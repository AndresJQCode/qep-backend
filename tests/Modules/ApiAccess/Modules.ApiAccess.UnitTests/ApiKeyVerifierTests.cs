using System.Globalization;
using BuildingBlocks.Application;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ApiAccess.UnitTests;

/// <summary>Spec 2026-11-01 parte 1: un rechazo sólo nombra la clave si quien llama probó tener su secreto. El prefijo es público, y con
/// él solo nadie puede escribir en el registro de un tenant.</summary>
public sealed class ApiKeyVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();

    private static (ApiKey Key, string Plaintext) NewKey(DateTimeOffset? expiresAt = null)
    {
        var material = ApiKeyMaterial.Generate();
        var key = ApiKey.Create(Tenant, "ERP", material, ["whatsapp.templates.send"], null, expiresAt, Member, Now);
        return (key, material.Plaintext);
    }

    private static string WithWrongSecret(string plaintext) => plaintext[..13] + new string('A', 43);

    private static Task<ApiKeyVerification> VerifyAsync(ApiKey key, string presented, DateTimeOffset now, TenantStatus status = TenantStatus.Active) =>
        new ApiKeyVerifier(new SingleKey(key), new Tenants(status), new FixedClock(now)).VerifyAsync(presented, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARevokedKeyWithItsSecretIsNamed()
    {
        var (key, plaintext) = NewKey();
        key.Revoke(Member, Now);

        var verification = await VerifyAsync(key, plaintext, Now);

        Assert.Equal(ApiKeyRejection.Revoked, verification.Rejection);
        Assert.Equal(new RejectedApiKey(key.Id.Value, "ERP", Tenant), verification.RejectedKey);
    }

    [Fact]
    public async Task AnExpiredKeyWithItsSecretIsNamed()
    {
        var (key, plaintext) = NewKey(Now.AddDays(1));

        var verification = await VerifyAsync(key, plaintext, Now.AddDays(2));

        Assert.Equal(ApiKeyRejection.Expired, verification.Rejection);
        Assert.Equal(new RejectedApiKey(key.Id.Value, "ERP", Tenant), verification.RejectedKey);
    }

    [Fact]
    public async Task AWrongSecretNeverNamesTheKeyEvenIfItIsRevoked()
    {
        var (key, plaintext) = NewKey();
        key.Revoke(Member, Now);

        var verification = await VerifyAsync(key, WithWrongSecret(plaintext), Now);

        Assert.Equal(ApiKeyRejection.SecretMismatch, verification.Rejection);
        Assert.Null(verification.RejectedKey);
    }

    [Fact]
    public async Task ASuspendedTenantDoesNotNameTheKey()
    {
        var (key, plaintext) = NewKey();

        var verification = await VerifyAsync(key, plaintext, Now, TenantStatus.Suspended);

        Assert.Equal(ApiKeyRejection.TenantNotActive, verification.Rejection);
        Assert.Null(verification.RejectedKey);
    }

    [Fact]
    public async Task AValidKeyHasNoRejectedKey()
    {
        var (key, plaintext) = NewKey();

        var verification = await VerifyAsync(key, plaintext, Now);

        Assert.NotNull(verification.Key);
        Assert.Null(verification.RejectedKey);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class SingleKey(ApiKey key) : IApiKeyRepository
    {
        public void Add(ApiKey key) => throw new NotSupportedException();

        public Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) => Task.FromResult<ApiKey?>(key);

        public Task TouchLastUsedAsync(ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Tenants(TenantStatus status) : ITenantDirectory
    {
        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult<TenantStatus?>(status);

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
