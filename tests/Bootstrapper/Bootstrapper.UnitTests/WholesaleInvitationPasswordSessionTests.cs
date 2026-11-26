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
/// con ella, por el mismo camino del login. Ruling W17: la contraseña se valida antes de tocar Identity. Sin código válido la
/// respuesta no dice si el correo tiene cuenta ni contraseña.
/// </summary>
public sealed class WholesaleInvitationPasswordSessionTests
{
    private const string Email = "ana@distri.co";
    private const string Password = "Distri-2026!";
    private const string Code = "123456";
    private const string Terms = "2026-11-19";
    private const string Correlation = "trace-1";

    private readonly FakeIdentity _identity = new();
    private readonly FakeTenancyUnitOfWork _tenancy = new();
    private TermsOptions _terms = new() { CurrentVersion = Terms };

    private WholesaleInvitationPasswordSession Session() =>
        new(_tenancy, _identity, _identity, _identity, _identity, _identity, _identity, _terms, new NewPasswordValidator());

    private Task<AuthenticatedUser> OpenAsync(
        string email, string password, string? code, string? termsVersion, bool emailProven = false) =>
        Session().OpenAsync(email, password, code, emailProven, termsVersion, Correlation, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("")]
    [InlineData("corta")]
    public async Task AWeakPasswordIsRejectedBeforeTouchingIdentity(string password)
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<ValidationException>(() => OpenAsync(Email, password, Code, Terms));

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

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => OpenAsync(Email, Password, code, Terms));

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
        var existing = _identity.AddUser(Email, password: null);

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => OpenAsync(Email, Password, code: null, Terms));

        Assert.Equal("identity.credentials.invalid", error.Code);
        Assert.DoesNotContain(_identity.Accepted, accepted => accepted.UserId == existing);
        Assert.Equal(0, _tenancy.Commits);
    }

    // Revisión del bloque C: sin código, un correo sin cuenta y una cuenta con contraseña y una clave errada responden igual, y
    // los dos pasan por el login (mismo hash, misma auditoría de intento fallido). Antes, el 422 de TermsVersion delataba que el
    // correo no tenía contraseña.
    [Fact]
    public async Task WithoutACodeAnUnknownEmailAndAWrongPasswordLookTheSame()
    {
        _identity.AddUser("con-clave@distri.co", Password);

        var unknown = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            OpenAsync("nadie@distri.co", Password, code: null, termsVersion: null));
        var wrong = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            OpenAsync("con-clave@distri.co", "Otra-Clave-99", code: null, termsVersion: null));

        Assert.Equal((unknown.GetType(), unknown.Code), (wrong.GetType(), wrong.Code));
        Assert.Equal("identity.credentials.invalid", unknown.Code);
        Assert.Equal(["nadie@distri.co", "con-clave@distri.co"], _identity.Logins);
        Assert.Empty(_identity.Provisioned);
    }

    // Con un código inventado tampoco: la versión de los términos se mira después de gastar un código válido, que sólo tiene el
    // dueño del buzón.
    [Theory]
    [InlineData(null)]
    [InlineData("2025-01-01")]
    public async Task AFakeCodeIs401WhateverTheTermsVersion(string? termsVersion)
    {
        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            OpenAsync("nadie@distri.co", Password, "000000", termsVersion));

        Assert.Equal("identity.credentials.invalid", error.Code);
    }

    [Fact]
    public async Task AUserWithAPasswordEntersThroughTheLoginWithoutCode()
    {
        var existing = _identity.AddUser(Email, Password);

        var user = await OpenAsync(Email, Password, code: null, termsVersion: null);

        Assert.Equal(new AuthenticatedUser(existing, Email), user);
        Assert.Equal([Email], _identity.Logins);
        Assert.Empty(_identity.Accepted);
        Assert.Empty(_identity.Consumed);
        Assert.Empty(_identity.Recorded);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    // Chequeo (b) de la revisión: verificar una contraseña existente no marca el correo como verificado. Una cuenta con correo
    // sin verificar recibe lo mismo que en el login (403 identity.email.not_verified), sin AcceptInvitationAsync.
    [Fact]
    public async Task AnUnverifiedPasswordAccountIsNotVerifiedByTheLink()
    {
        _identity.AddUser(Email, Password, emailVerified: false);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => OpenAsync(Email, Password, Code, Terms));

        Assert.Equal(PasswordLoginService.EmailNotVerifiedCode, error.Code);
        Assert.Empty(_identity.Accepted);
        Assert.Empty(_identity.Consumed);
        Assert.Equal(0, _tenancy.Commits);
    }

    [Fact]
    public async Task ANewUserNeedsTheTermsVersion()
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<ValidationException>(() => OpenAsync(Email, Password, Code, termsVersion: null));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "TermsVersion");
        Assert.Empty(_identity.Provisioned);
        Assert.Empty(_identity.Accepted);
        Assert.Equal(0, _tenancy.Commits);
    }

    [Fact]
    public async Task AStaleTermsVersionCreatesNothing()
    {
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<IdentityDomainException>(() => OpenAsync(Email, Password, Code, "2025-01-01"));

        Assert.Equal(TermsAcceptanceService.VersionMismatchCode, error.Code);
        Assert.Empty(_identity.Provisioned);
        Assert.Empty(_identity.Accepted);
    }

    [Fact]
    public async Task WithoutAConfiguredTermsVersionANewUserFailsClosed()
    {
        _terms = new TermsOptions();
        _identity.IssueCode(Email, Code);

        var error = await Assert.ThrowsAsync<IdentityDomainException>(() => OpenAsync(Email, Password, Code, Terms));

        Assert.Equal(TermsAcceptanceService.UnavailableCode, error.Code);
        Assert.Empty(_identity.Provisioned);
    }

    // Ruling del bloque A: RecordAsync se confirma en su propia conexión y tiene FK a identity.users. El doble falla si se llama
    // para un usuario que todavía no está guardado, así que esta prueba se cae si el orden se invierte.
    [Fact]
    public async Task ANewUserWithACodeAndTheTermsGetsThePasswordAndThenTheTerms()
    {
        _identity.IssueCode(Email, Code);

        var user = await OpenAsync(Email, Password, Code, Terms);

        var provisioned = Assert.Single(_identity.Provisioned);
        Assert.Equal(new AuthenticatedUser(provisioned, Email), user);
        Assert.Equal([(Email, Code)], _identity.Consumed);
        Assert.Equal([(provisioned, Password)], _identity.Accepted);
        Assert.Equal([(provisioned, Terms)], _identity.Recorded);
        Assert.Equal(["consume", "provision", "password", "terms"], _identity.Calls.Where(call => call != "lookup" && call != "has-password"));
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    // Spec 2026-11-24: el link del correo prueba el buzón, así que la cuenta nace sin código, con la contraseña y los términos.
    [Fact]
    public async Task AnEmailProofCreatesTheAccountWithoutACodeAndRecordsTheTerms()
    {
        var user = await OpenAsync(Email, Password, code: null, Terms, emailProven: true);

        var provisioned = Assert.Single(_identity.Provisioned);
        Assert.Equal(new AuthenticatedUser(provisioned, Email), user);
        Assert.Empty(_identity.Consumed);
        Assert.Equal([(provisioned, Password)], _identity.Accepted);
        Assert.Equal([(provisioned, Terms)], _identity.Recorded);
        Assert.Equal(["provision", "password", "terms"], _identity.Calls.Where(call => call != "lookup" && call != "has-password"));
        Assert.Equal((1, 1, Email), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    [Fact]
    public async Task AnEmailProofGivesAPasswordToAnExistingAccountWithoutOne()
    {
        var existing = _identity.AddUser(Email, password: null);

        var user = await OpenAsync(Email, Password, code: null, Terms, emailProven: true);

        Assert.Equal(new AuthenticatedUser(existing, Email), user);
        Assert.Equal([(existing, Password)], _identity.Accepted);
        Assert.Empty(_identity.Provisioned);
    }

    // Anti-objetivo del spec 2026-11-24: con la contraseña ya creada sigue siendo un inicio de sesión; la prueba no la cambia ni
    // deja entrar con otra.
    [Fact]
    public async Task AnEmailProofDoesNotTouchAnAccountThatHasAPassword()
    {
        var existing = _identity.AddUser(Email, Password);

        var error = await Assert.ThrowsAsync<RequestUnauthorizedException>(() =>
            OpenAsync(Email, "Otra-Clave-99", code: null, Terms, emailProven: true));

        Assert.Equal("identity.credentials.invalid", error.Code);
        Assert.Empty(_identity.Accepted);

        var user = await OpenAsync(Email, Password, code: null, Terms, emailProven: true);

        Assert.Equal(new AuthenticatedUser(existing, Email), user);
        Assert.Equal([Email, Email], _identity.Logins);
        Assert.Empty(_identity.Accepted);
        Assert.Empty(_identity.Recorded);
        Assert.Empty(_identity.Provisioned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-01-01")]
    public async Task AnEmailProofStillNeedsTheCurrentTerms(string? termsVersion)
    {
        var error = await Record.ExceptionAsync(() =>
            OpenAsync(Email, Password, code: null, termsVersion, emailProven: true));

        if (termsVersion is null)
        {
            var validation = Assert.IsType<ValidationException>(error);
            Assert.Contains(validation.Errors, failure => failure.PropertyName == "TermsVersion");
        }
        else
        {
            Assert.Equal(TermsAcceptanceService.VersionMismatchCode, Assert.IsType<IdentityDomainException>(error).Code);
        }

        Assert.Empty(_identity.Provisioned);
        Assert.Empty(_identity.Accepted);
        Assert.Empty(_identity.Recorded);
        Assert.Equal(0, _tenancy.Commits);
    }

    /// <summary>
    /// Identity en memoria. Aprovisionar guarda en el acto, como <c>IdentityProvisioningService</c>; <see cref="RecordAsync"/>
    /// exige que el usuario ya esté guardado, como la FK de <c>identity.terms_acceptances</c>; <see cref="LoginAsync"/> sigue las
    /// reglas de <c>PasswordLoginService</c> (401 igual para todo fallo, 403 con el correo sin verificar).
    /// </summary>
    private sealed class FakeIdentity :
        IIdentityProvisioning, IPasswordCredentials, IPasswordLogin, IUserDirectory, IEmailChallenges, ITermsAcceptances
    {
        private readonly Dictionary<Guid, string> _users = [];
        private readonly Dictionary<Guid, string> _passwords = [];
        private readonly HashSet<Guid> _unverified = [];
        private readonly Dictionary<string, string> _codes = [];

        public List<string> Calls { get; } = [];

        public List<Guid> Provisioned { get; } = [];

        public List<(Guid UserId, string Password)> Accepted { get; } = [];

        public List<string> Logins { get; } = [];

        public List<(string Email, string Code)> Consumed { get; } = [];

        public List<(Guid UserId, string Version)> Recorded { get; } = [];

        public Guid AddUser(string email, string? password, bool emailVerified = true)
        {
            var id = Guid.CreateVersion7();
            _users[id] = email;
            if (password is not null)
            {
                _passwords[id] = password;
            }

            if (!emailVerified)
            {
                _unverified.Add(id);
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

            var id = AddUser(email, password: null);
            Provisioned.Add(id);
            return Task.FromResult(id);
        }

        public Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls.Add("has-password");
            return Task.FromResult(_passwords.ContainsKey(userId));
        }

        public Task<AuthenticatedUser> AcceptInvitationAsync(Guid userId, string password, CancellationToken cancellationToken)
        {
            Calls.Add("password");
            Accepted.Add((userId, password));
            _passwords[userId] = password;
            return Task.FromResult(new AuthenticatedUser(userId, _users[userId]));
        }

        public Task<AuthenticatedUser> LoginAsync(
            string email, string password, string correlationId, CancellationToken cancellationToken)
        {
            Calls.Add("login");
            Logins.Add(email);
            if (Find(email) is not { } userId || !_passwords.TryGetValue(userId, out var stored) || stored != password)
            {
                throw new RequestUnauthorizedException(PasswordLoginService.InvalidCredentialsCode, "Invalid credentials.");
            }

            if (_unverified.Contains(userId))
            {
                throw new RequestForbiddenException(PasswordLoginService.EmailNotVerifiedCode, "The email is not verified.");
            }

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
