using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Ruling W17: <c>session-with-password</c> valida la contraseña con las reglas del registro antes de crear al usuario, así una
/// contraseña débil nunca deja un usuario sin membresía.
/// </summary>
public sealed class WholesaleInvitationPasswordSessionTests
{
    private const string Email = "ana@distri.co";

    private readonly FakeProvisioning _provisioning = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakeTenancyUnitOfWork _tenancy = new();

    private WholesaleInvitationPasswordSession Session() =>
        new(_tenancy, _provisioning, _credentials, new NewPasswordValidator());

    [Theory]
    [InlineData("")]
    [InlineData("corta")]
    public async Task AWeakPasswordFailsValidationAndNeverCreatesTheUser(string password)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Session().OpenAsync(Email, password, TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Password" && failure.ErrorCode == "identity.password.invalid");
        Assert.Empty(_provisioning.Provisioned);
        Assert.Empty(_credentials.Accepted);
        Assert.Equal(0, _tenancy.Scopes);
    }

    [Fact]
    public async Task AValidPasswordProvisionsAcceptsAndCommitsUnderTheLifecycleLock()
    {
        var user = await Session().OpenAsync(Email, "Distri-2026!", TestContext.Current.CancellationToken);

        Assert.Equal([Email], _provisioning.Provisioned);
        Assert.Equal([(_provisioning.UserId, "Distri-2026!")], _credentials.Accepted);
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
        Assert.Equal(new AuthenticatedUser(_provisioning.UserId, Email), user);
    }

    private sealed class FakeProvisioning : IIdentityProvisioning
    {
        public Guid UserId { get; } = Guid.CreateVersion7();

        public List<string> Provisioned { get; } = [];

        public Task<Guid> GetOrProvisionInvitedUserAsync(string email, CancellationToken cancellationToken)
        {
            Provisioned.Add(email);
            return Task.FromResult(UserId);
        }
    }

    private sealed class FakeCredentials : IPasswordCredentials
    {
        public List<(Guid UserId, string Password)> Accepted { get; } = [];

        public Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<AuthenticatedUser> AcceptInvitationAsync(Guid userId, string password, CancellationToken cancellationToken)
        {
            Accepted.Add((userId, password));
            return Task.FromResult(new AuthenticatedUser(userId, Email));
        }
    }

    private sealed class FakeTenancyUnitOfWork : ITenancyUnitOfWork
    {
        public int Scopes { get; private set; }

        public int Commits { get; private set; }

        public string? LockedEmail { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(string email, CancellationToken cancellationToken)
        {
            Scopes++;
            LockedEmail = email;
            return Task.FromResult<IUserLifecycleScope>(new Scope(this));
        }

        public Task<ITenantChangeScope> BeginTenantChangeScopeAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class Scope(FakeTenancyUnitOfWork owner) : IUserLifecycleScope
        {
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                owner.Commits++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
