namespace Modules.Wholesale.Domain;

/// <summary>
/// La relación del mayorista con un distribuidor: nace <see cref="DistributorStatus.Invited"/> con su invitación, pasa a
/// <see cref="DistributorStatus.Active"/> al aceptar y se puede bloquear y desbloquear. Borrar sólo vale mientras está
/// invitada (D-14); no hay «terminar» en esta fase. Toda mutación sube <see cref="Version"/> (el <c>If-Match</c> del
/// endpoint) y fija <see cref="UpdatedAt"/>; una transición fuera de estado es
/// <c>wholesale.distributor.invalid_transition</c>. Los textos llegan validados por Application; acá sólo se recortan.
/// </summary>
public sealed class Distributor
{
    /// <summary>La misma vigencia que la invitación de un miembro (<c>Membership.DefaultInvitationTimeToLive</c>).</summary>
    public static readonly TimeSpan InvitationTimeToLive = TimeSpan.FromHours(72);

    private const int TokenHashLength = 64;

    // EF Core materializa por acá; Invite es el único punto de entrada de código.
    private Distributor()
    {
        BusinessName = string.Empty;
        IdentificationNumber = string.Empty;
        ContactName = string.Empty;
        Email = string.Empty;
        Invitation = null!;
    }

    public DistributorId Id { get; private set; }

    /// <summary>El tenant del mayorista: dueño de la relación.</summary>
    public Guid TenantId { get; private set; }

    public string BusinessName { get; private set; }

    /// <summary>El documento (NIT) del distribuidor; uno por mayorista en cualquier estado (D-13).</summary>
    public string IdentificationNumber { get; private set; }

    public string ContactName { get; private set; }

    /// <summary>Recortado y en minúsculas: el correo al que se ata la invitación.</summary>
    public string Email { get; private set; }

    public PriceListId PriceListId { get; private set; }

    /// <summary>La membresía del asesor en el tenant del mayorista (D-15).</summary>
    public Guid AdvisorMemberId { get; private set; }

    /// <summary>La empresa facturadora del mayorista que da la identidad del proveedor vinculado (D-2).</summary>
    public Guid BillingCompanyId { get; private set; }

    public DistributorStatus Status { get; private set; }

    /// <summary>El tenant del distribuidor, desde que acepta.</summary>
    public Guid? DistributorTenantId { get; private set; }

    /// <summary>El cliente del distribuidor en el tenant del mayorista, desde que acepta (D-7).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>Traza, no puntero vivo (D-19).</summary>
    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public long Version { get; private set; }

    public DistributorInvitation Invitation { get; private set; }

    public static Distributor Invite(
        Guid tenantId,
        string email,
        string businessName,
        string identificationNumber,
        string contactName,
        PriceListId priceListId,
        Guid advisorMemberId,
        Guid billingCompanyId,
        Guid invitedByUserId,
        string tokenHash,
        Guid reservedTenantId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identificationNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactName);
        EnsureTokenHash(tokenHash);
        if (reservedTenantId == Guid.Empty)
        {
            throw new ArgumentException("The reserved tenant id cannot be empty.", nameof(reservedTenantId));
        }

        return new Distributor
        {
            Id = DistributorId.New(),
            TenantId = tenantId,
            Email = NormalizeEmail(email),
            BusinessName = businessName.Trim(),
            IdentificationNumber = identificationNumber.Trim(),
            ContactName = contactName.Trim(),
            PriceListId = priceListId,
            AdvisorMemberId = advisorMemberId,
            BillingCompanyId = billingCompanyId,
            InvitedByUserId = invitedByUserId,
            Status = DistributorStatus.Invited,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
            Invitation = DistributorInvitation.Issue(tokenHash, reservedTenantId, now),
        };
    }

    /// <summary>Lista, asesor y empresa facturadora; en cualquier estado.</summary>
    public void ChangeTerms(PriceListId priceListId, Guid advisorMemberId, Guid billingCompanyId, DateTimeOffset now)
    {
        PriceListId = priceListId;
        AdvisorMemberId = advisorMemberId;
        BillingCompanyId = billingCompanyId;
        Touch(now);
    }

    /// <summary>Token y vigencia nuevos (72 h desde ahora); el link anterior deja de existir. Sólo mientras está invitada,
    /// aunque la invitación ya haya vencido: reenviar es como se renueva.</summary>
    public void ResendInvitation(string tokenHash, DateTimeOffset now)
    {
        EnsureTokenHash(tokenHash);
        EnsureStatus(DistributorStatus.Invited);
        Invitation.Rotate(tokenHash, now);
        Touch(now);
    }

    public void Block(DateTimeOffset now)
    {
        EnsureStatus(DistributorStatus.Active);
        Status = DistributorStatus.Blocked;
        Touch(now);
    }

    public void Unblock(DateTimeOffset now)
    {
        EnsureStatus(DistributorStatus.Blocked);
        Status = DistributorStatus.Active;
        Touch(now);
    }

    /// <summary>Borrar es definitivo y sólo vale mientras está invitada (D-14): el link pasa a 404.</summary>
    public void EnsureDeletable() => EnsureStatus(DistributorStatus.Invited);

    public InvitationViewState InvitationState(DateTimeOffset now) => Status switch
    {
        DistributorStatus.Invited when Invitation.IsExpiredAt(now) => InvitationViewState.Expired,
        DistributorStatus.Invited => InvitationViewState.Pending,
        _ => InvitationViewState.Accepted,
    };

    /// <summary>Si el correo de la sesión es el de la invitación, sin importar mayúsculas ni espacios alrededor.</summary>
    public bool InvitedEmailMatches(string? email) =>
        !string.IsNullOrWhiteSpace(email) && string.Equals(email.Trim(), Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Consume la invitación y activa la relación. Sólo invitada y sin vencer: una relación ya aceptada (activa o bloqueada)
    /// es <c>wholesale.invitation.already_accepted</c>, una vencida es <c>wholesale.invitation.expired</c>.
    /// </summary>
    public void Accept(Guid userId, Guid distributorTenantId, Guid customerId, DateTimeOffset now)
    {
        if (Status != DistributorStatus.Invited)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.InvitationAlreadyAccepted, "La invitación ya fue aceptada.");
        }

        if (Invitation.IsExpiredAt(now))
        {
            throw new WholesaleDomainException(WholesaleErrorCodes.InvitationExpired, "La invitación venció.");
        }

        Status = DistributorStatus.Active;
        DistributorTenantId = distributorTenantId;
        CustomerId = customerId;
        ActivatedAt = now;
        Invitation.MarkAccepted(userId, now);
        Touch(now);
    }

    /// <summary>Lo que el estado permite, en el orden del contrato. No mira permisos: sin
    /// <c>wholesale.distributor.manage</c> la respuesta HTTP lo vacía.</summary>
    public IReadOnlyList<DistributorAction> AllowedActions() => Status switch
    {
        DistributorStatus.Invited => [DistributorAction.Edit, DistributorAction.Resend, DistributorAction.Delete],
        DistributorStatus.Active => [DistributorAction.Edit, DistributorAction.Block],
        DistributorStatus.Blocked => [DistributorAction.Edit, DistributorAction.Unblock],
        _ => [],
    };

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Ruling W11 (c): la última red sobre el campo más sensible de la invitación. Sólo se guarda la forma de
    /// <c>InvitationTokens.HashOf</c> (SHA-256 en hex minúsculo, 64 caracteres); un token plano no la tiene, así que nunca
    /// llega a la base aunque quien llama se equivoque de variable.
    /// </summary>
    private static void EnsureTokenHash(string tokenHash)
    {
        if (tokenHash is not { Length: TokenHashLength } || !tokenHash.All(IsLowercaseHex))
        {
            throw new ArgumentException("The token hash must be a lowercase hex SHA-256.", nameof(tokenHash));
        }

        static bool IsLowercaseHex(char value) => value is (>= '0' and <= '9') or (>= 'a' and <= 'f');
    }

    private void EnsureStatus(DistributorStatus expected)
    {
        if (Status != expected)
        {
            throw new WholesaleDomainException(
                WholesaleErrorCodes.DistributorInvalidTransition,
                $"El distribuidor en estado {Status} no admite esa acción.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
