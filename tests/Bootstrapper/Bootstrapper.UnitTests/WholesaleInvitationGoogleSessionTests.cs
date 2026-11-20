using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>POST /wholesale-invitations/{token}/session</c> con Google: la sesión se abre para el usuario que el subject ya tiene o,
/// si no tiene ninguno, para el que se aprovisiona por el <b>correo verificado de Google</b> (spec 2026-11-19: el link ya no
/// exige el correo invitado). Nunca se aprovisiona un usuario que después no se usa.
/// </summary>
public sealed class WholesaleInvitationGoogleSessionTests
{
    private const string GoogleEmail = "ana@gmail.com";
    private const string Subject = "google-sub-1";

    private readonly FakeIdentity _identity = new();
    private readonly FakeTenancyUnitOfWork _tenancy = new();

    private WholesaleInvitationGoogleSession Session() => new(_tenancy, _identity, _identity);

    [Fact]
    public async Task ASubjectAlreadyLinkedUsesThatUserAndProvisionsNothing()
    {
        var existing = _identity.AddUser(GoogleEmail, Subject);

        var outcome = await Session().OpenAsync(GoogleEmail, Subject, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderLinkOutcome.Linked(existing), outcome);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal((1, 1, GoogleEmail), (_tenancy.Scopes, _tenancy.Commits, _tenancy.LockedEmail));
    }

    // Spec 2026-11-19 §6: la invitación fue a otro correo; la cuenta es la de Google y su correo reemplaza al invitado al aceptar.
    [Fact]
    public async Task GoogleOpensTheSessionForTheVerifiedEmailEvenIfItIsNotTheInvitedOne()
    {
        var outcome = await Session().OpenAsync(GoogleEmail, Subject, TestContext.Current.CancellationToken);

        var provisioned = Assert.Single(_identity.Provisioned);
        Assert.Equal(ProviderLinkOutcome.Linked(provisioned), outcome);
        Assert.Equal(GoogleEmail, _identity.EmailOf(provisioned));
        Assert.Equal(provisioned, _identity.UserForSubject(Subject));
        Assert.Equal((1, GoogleEmail), (_tenancy.Commits, _tenancy.LockedEmail));
    }

    [Fact]
    public async Task AnEmailWithAUserAndANewSubjectLinksThatUserWithoutProvisioning()
    {
        var existing = _identity.AddUser(GoogleEmail, subject: null);

        var outcome = await Session().OpenAsync(GoogleEmail, Subject, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderLinkOutcome.Linked(existing), outcome);
        Assert.Empty(_identity.Provisioned);
    }

    // Antes era email_mismatch. Ahora el subject manda: la sesión es la de su cuenta, y nada se aprovisiona.
    [Fact]
    public async Task ASubjectLinkedToAnAccountWithAnotherEmailOpensThatAccount()
    {
        var existing = _identity.AddUser("otra@distri.co", Subject);

        var outcome = await Session().OpenAsync(GoogleEmail, Subject, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderLinkOutcome.Linked(existing), outcome);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal(1, _tenancy.Commits);
    }

    [Fact]
    public async Task ADenialOtherThanAMissingUserIsReturnedWithoutProvisioning()
    {
        _identity.DenyWith = "email_invalid";

        var outcome = await Session().OpenAsync(GoogleEmail, Subject, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderLinkOutcome.Denied("email_invalid"), outcome);
        Assert.Empty(_identity.Provisioned);
        Assert.Equal(0, _tenancy.Commits);
    }

    /// <summary>Las reglas de <c>ProviderLinkingService</c> e <c>IdentityProvisioningService</c> sobre un directorio en memoria.</summary>
    private sealed class FakeIdentity : IIdentityProvisioning, IProviderLinking
    {
        private readonly Dictionary<Guid, string> _emails = [];
        private readonly Dictionary<string, Guid> _subjects = [];

        public List<Guid> Provisioned { get; } = [];

        public string? DenyWith { get; set; }

        public Guid AddUser(string email, string? subject)
        {
            var id = Guid.CreateVersion7();
            _emails[id] = email;
            if (subject is not null)
            {
                _subjects[subject] = id;
            }

            return id;
        }

        public string? EmailOf(Guid userId) => _emails.TryGetValue(userId, out var email) ? email : null;

        public Guid? UserForSubject(string subject) => _subjects.TryGetValue(subject, out var id) ? id : null;

        public Task<Guid> GetOrProvisionInvitedUserAsync(string email, CancellationToken cancellationToken)
        {
            if (FindByEmail(email) is { } existing)
            {
                return Task.FromResult(existing);
            }

            var id = AddUser(email, subject: null);
            Provisioned.Add(id);
            return Task.FromResult(id);
        }

        public Task<ProviderLinkOutcome> LinkAndActivateAsync(
            string provider, string subject, string? email, bool emailVerified, CancellationToken cancellationToken)
        {
            if (_subjects.TryGetValue(subject, out var linked))
            {
                return Task.FromResult(ProviderLinkOutcome.Linked(linked));
            }

            if (DenyWith is not null)
            {
                return Task.FromResult(ProviderLinkOutcome.Denied(DenyWith));
            }

            if (FindByEmail(email) is not { } user)
            {
                return Task.FromResult(ProviderLinkOutcome.Denied("invitation_required"));
            }

            _subjects[subject] = user;
            return Task.FromResult(ProviderLinkOutcome.Linked(user));
        }

        private Guid? FindByEmail(string? email) =>
            _emails.Where(pair => string.Equals(pair.Value, email, StringComparison.OrdinalIgnoreCase))
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
