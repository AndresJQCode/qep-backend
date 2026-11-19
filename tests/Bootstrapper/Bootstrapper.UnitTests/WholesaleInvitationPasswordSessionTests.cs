using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>session-with-password</c> desde el link (spec 2026-11-19 §4). El link ya no prueba el buzón: una cuenta sin contraseña (o
/// sin usuario) necesita siempre un código válido del correo y la versión de los términos; sólo quien ya tiene contraseña entra
/// con ella. Ruling W17: la contraseña se valida antes de tocar Identity.
/// </summary>
public sealed class WholesaleInvitationPasswordSessionTests
{
    private const string Email = "ana@distri.co";
    private const string Password = "Distri-2026!";
    private const string Code = "123456";
    private const string Terms = "2026-11-19";

    private readonly FakeIdentity _identity = new();
    private readonly FakeTenancyUnitOfWork _tenancy = new();
    private TermsOptions _terms = new() { CurrentVersion = Terms };

    private WholesaleInvitationPasswordSession Session() =>
        new(_tenancy, _identity, _identity, _identity, _identity, _identity, _terms, new NewPasswordValidator());

    [Theory]
    [InlineData("")]
    [InlineData("corta")]
    public async Task AWeakPasswordIsRejectedBeforeTouchingIdentity(string password)
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Session().OpenAsync(Email, password, Code, Terms, TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Password" && failure.ErrorCode == "identity.password.invalid");
        Assert.Empty(_identity.Calls);
        Assert.Equal(0, _tenancy.Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("999999")]
    public async Task AUserWithoutAPasswordNeedsAValidCode(string? code)
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            Session().OpenAsync(Email, Password, code, Terms, TestContext.Current.CancellationToken));

        Assert.Equal("identity.credentials.invalid", error.Code);
        Assert.Empty(_identity.Provisioned);
        Assert.Empty(_identity.Accepted);
        Assert.Empty(_identity.Recorded);
        Assert.Equal(0, _tenancy.Commits);
    }

    // Foco 1: una cuenta que existe sin contraseña (invitada como miembro, o entra con Google) tampoco recibe una sin código;
    // si no, el link más el correo de la víctima le fijarían la contraseña.
    [Fact]
    public async Task AnExistingUserWithoutAPasswordAlsoNeedsTheCode()
    {
        var existing = _identity.AddUser(Email, withPassword: false);

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            Session().OpenAsync(Email, Password, code: null, Terms, TestContext.Current.CancellationToken));

        Assert.Equal("identity.credentials.invalid", error.Code);
        Assert.DoesNotContain(_identity.Accepted, accepted => accepted.UserId == existing);
        Assert.Equal(0, _tenancy.Commits);
    }

    [Fact]
    public async Task AUserWithAPasswordEntersWithoutCode()
    {
        var existing = _identity.AddUser(Email, withPassword: true);

        var user = await Session().OpenAsync(Email, Password, code: null, termsVersion: null, TestContext.Current.CancellationToken);

        Assert.Equal(new AuthenticatedUser(existing, Email), user);
        Assert.Equal([(existing, Password)], _identity.Accepted);
        Assert.Empty(_identity.Consumed);
        Assert.Empty(_identity.Recorded);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    [Fact]
    public async Task ANewUserNeedsTheTermsVersion()
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Session().OpenAsync(Email, Password, Code, termsVersion: null, TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "TermsVersion");
        // El código no se gasta: con la versión, el mismo código sirve.
        Assert.Empty(_identity.Consumed);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal(0, _tenancy.Commits);
    }

    [Fact]
    public async Task AStaleTermsVersionIsRejectedBeforeSpendingTheCode()
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<IdentityDomainException>(() =>
            Session().OpenAsync(Email, Password, Code, "2025-01-01", TestContext.Current.CancellationToken));

        Assert.Equal(TermsAcceptanceService.VersionMismatchCode, error.Code);
        Assert.Empty(_identity.Consumed);
        Assert.Empty(_identity.Provisioned);
    }

    [Fact]
    public async Task WithoutAConfiguredTermsVersionANewUserFailsClosed()
    {
        _terms = new TermsOptions();
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<IdentityDomainException>(() =>
            Session().OpenAsync(Email, Password, Code, Terms, TestContext.Current.CancellationToken));

        Assert.Equal(TermsAcceptanceService.UnavailableCode, error.Code);
        Assert.Empty(_identity.Consumed);
        Assert.Empty(_identity.Provisioned);
    }

    // Ruling del bloque A: RecordAsync se confirma en su propia conexión y tiene FK a identity.users. El doble falla si se llama
    // para un usuario que todavía no está guardado, así que esta prueba se cae si el orden se invierte.
    [Fact]
    public async Task ANewUserWithACodeAndTheTermsGetsThePasswordAndThenTheTerms()
    {
        _identity.IssueCode(Email, Code);

        var user = await Session().OpenAsync(Email, Password, Code, Terms, TestContext.Current.CancellationToken);

        var provisioned = Assert.Single(_identity.Provisioned);
        Assert.Equal(new AuthenticatedUser(provisioned, Email), user);
        Assert.Equal([(Email, Code)], _identity.Consumed);
        Assert.Equal([(provisioned, Password)], _identity.Accepted);
        Assert.Equal([(provisioned, Terms)], _identity.Recorded);
        Assert.Equal(["consume", "provision", "password", "terms"], _identity.Calls.Where(call => call != "lookup" && call != "has-password"));
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    /// <summary>
    /// Identity en memoria. Aprovisionar guarda en el acto, como <c>IdentityProvisioningService</c>; <see cref="RecordAsync"/>
    /// exige que el usuario ya esté guardado, como la FK de <c>identity.terms_acceptances</c>.
    /// </summary>
    private sealed class FakeIdentity :
        IIdentityProvisioning, IPasswordCredentials, IUserDirectory, IEmailChallenges, ITermsAcceptances
    {
        private readonly Dictionary<Guid, string> _users = [];
        private readonly HashSet<Guid> _withPassword = [];
        private readonly Dictionary<string, string> _codes = [];

        public List<string> Calls { get; } = [];

        public List<Guid> Provisioned { get; } = [];

        public List<(Guid UserId, string Password)> Accepted { get; } = [];

        public List<(string Email, string Code)> Consumed { get; } = [];

        public List<(Guid UserId, string Version)> Recorded { get; } = [];

        public Guid AddUser(string email, bool withPassword)
        {
            var id = Guid.CreateVersion7();
            _users[id] = email;
            if (withPassword)
            {
                _withPassword.Add(id);
            }

            return id;
        }

        public void IssueCode(string email, string code) => _codes[email] = code;

        public Task<Guid> GetOrProvisionInvitedUserAsync(string email, CancellationToken cancellationToken)
        {
            Calls.Add("provision");
            if (Find(email) is { } existing)
            {
                return Task.FromResult(existing);
            }

            var id = AddUser(email, withPassword: false);
            Provisioned.Add(id);
            return Task.FromResult(id);
        }

        public Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls.Add("has-password");
            return Task.FromResult(_withPassword.Contains(userId));
        }

        public Task<AuthenticatedUser> AcceptInvitationAsync(Guid userId, string password, CancellationToken cancellationToken)
        {
            Calls.Add("password");
            Accepted.Add((userId, password));
            _withPassword.Add(userId);
            return Task.FromResult(new AuthenticatedUser(userId, _users[userId]));
        }

        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_users.TryGetValue(userId, out var email) ? email : null);

        public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
        {
            Calls.Add("lookup");
            return Task.FromResult(Find(email));
        }

        public Task IssueAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryConsumeAsync(string email, string code, CancellationToken cancellationToken)
        {
            Calls.Add("consume");
            if (_codes.TryGetValue(email, out var expected) && expected == code)
            {
                _codes.Remove(email);
                Consumed.Add((email, code));
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task RecordAsync(Guid userId, string version, CancellationToken cancellationToken)
        {
            if (!_users.ContainsKey(userId))
            {
                throw new InvalidOperationException("FK: the user is not saved yet.");
            }

            Calls.Add("terms");
            Recorded.Add((userId, version));
            return Task.CompletedTask;
        }

        public Task<bool> HasAcceptedCurrentAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Recorded.Any(record => record.UserId == userId));

        private Guid? Find(string email) =>
            _users.Where(pair => string.Equals(pair.Value, email, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();
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
