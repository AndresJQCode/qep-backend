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
        Country = string.Empty;
        IdentificationType = string.Empty;
        ContactName = string.Empty;
        Email = string.Empty;
        Invitation = null!;
    }

    public DistributorId Id { get; private set; }

    /// <summary>El tenant del mayorista: dueño de la relación.</summary>
    public Guid TenantId { get; private set; }

    public string BusinessName { get; private set; }

    /// <summary>ISO 3166-1 alfa-2 en mayúsculas: el país del distribuidor decide qué tipos de documento admite.</summary>
    public string Country { get; private set; }

    /// <summary>Código de cable del tipo de documento (<c>DocumentTypesByCountry.All</c>), en mayúsculas.</summary>
    public string IdentificationType { get; private set; }

    /// <summary>El número del documento del distribuidor; único por mayorista y tipo, en cualquier estado (D-13).</summary>
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

    /// <summary>
    /// El celular del contacto para el aviso por WhatsApp de la invitación (spec 2026-11-18), en E.164 con «+». Llega
    /// normalizado por Application; <c>null</c> = sin aviso.
    /// </summary>
    public string? Phone { get; private set; }

    /// <summary>El número de WhatsApp del mayorista que manda el aviso; sólo existe junto a <see cref="Phone"/>.</summary>
    public Guid? WhatsAppConnectionId { get; private set; }

    public DistributorInvitation Invitation { get; private set; }

    /// <summary>E.164: «+» y hasta 15 dígitos.</summary>
    public const int PhoneMaxLength = 16;

    public static Distributor Invite(
        Guid tenantId,
        string email,
        string businessName,
        string country,
        string identificationType,
        string identificationNumber,
        string contactName,
        PriceListId priceListId,
        Guid advisorMemberId,
        Guid billingCompanyId,
        Guid invitedByUserId,
        string tokenHash,
        Guid reservedTenantId,
        string? phone,
        Guid? whatsAppConnectionId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessName);
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        ArgumentException.ThrowIfNullOrWhiteSpace(identificationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(identificationNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactName);
        EnsureTokenHash(tokenHash);
        if (reservedTenantId == Guid.Empty)
        {
            throw new ArgumentException("The reserved tenant id cannot be empty.", nameof(reservedTenantId));
        }

        var (noticePhone, noticeConnection) = NoticeTarget(phone, whatsAppConnectionId);

        return new Distributor
        {
            Id = DistributorId.New(),
            TenantId = tenantId,
            Email = NormalizeEmail(email),
            BusinessName = businessName.Trim(),
            Country = country.Trim().ToUpperInvariant(),
            IdentificationType = identificationType.Trim().ToUpperInvariant(),
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
            Phone = noticePhone,
            WhatsAppConnectionId = noticeConnection,
            Invitation = DistributorInvitation.Issue(tokenHash, reservedTenantId, withNotice: noticePhone is not null, now),
        };
    }

    /// <summary>
    /// Teléfono y conexión del aviso, sólo mientras está invitada (P-2: afuera no hace nada y devuelve <c>false</c>). Con
    /// <paramref name="phone"/> nulo borra los dos (P-3). Si algo cambia sube la generación y deja el aviso en <c>null</c>
    /// hasta el próximo «Reenviar», y devuelve <c>true</c>. No sube <see cref="Version"/>: lo hace
    /// <see cref="ChangeTerms"/>, que corre en el mismo <c>PUT</c>.
    /// </summary>
    public bool ChangeNoticeTarget(string? phone, Guid? whatsAppConnectionId)
    {
        if (Status != DistributorStatus.Invited)
        {
            return false;
        }

        var (noticePhone, noticeConnection) = NoticeTarget(phone, whatsAppConnectionId);
        if (noticePhone == Phone && noticeConnection == WhatsAppConnectionId)
        {
            return false;
        }

        Phone = noticePhone;
        WhatsAppConnectionId = noticeConnection;
        Invitation.ForgetNotice();
        return true;
    }

    /// <summary>
    /// Registra el resultado del envío del aviso. Sin efecto (y <c>false</c>) si es de otra generación o si no hay aviso en
    /// curso (D-4). No sube <see cref="Version"/> ni toca <see cref="UpdatedAt"/> (D-3): el formulario abierto no da 412.
    /// </summary>
    public bool RecordInvitationNotice(int generation, InvitationNoticeStatus status, string? failureCode)
    {
        if (generation != Invitation.Generation || Invitation.NoticeStatus is null)
        {
            return false;
        }

        Invitation.SetNotice(status, failureCode);
        return true;
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
    /// aunque la invitación ya haya vencido: reenviar es como se renueva. Sube la generación del aviso: con teléfono queda
    /// pendiente si <paramref name="noticeQueued"/> (se escribió su evento) y si no, fallido con <c>whatsapp_unavailable</c>
    /// (la conexión guardada ya no está lista); sin teléfono no hay aviso.</summary>
    public void ResendInvitation(string tokenHash, bool noticeQueued, DateTimeOffset now)
    {
        EnsureTokenHash(tokenHash);
        EnsureStatus(DistributorStatus.Invited);
        Invitation.Rotate(tokenHash, now);
        if (Phone is null)
        {
            Invitation.SetNotice(null, null);
        }
        else if (noticeQueued)
        {
            Invitation.SetNotice(InvitationNoticeStatus.Pending, null);
        }
        else
        {
            Invitation.SetNotice(InvitationNoticeStatus.Failed, DistributorInvitation.WhatsAppUnavailableCode);
        }

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

    // Sin teléfono no hay conexión; con teléfono la conexión es obligatoria (la resuelve Application antes de llegar acá).
    private static (string? Phone, Guid? ConnectionId) NoticeTarget(string? phone, Guid? whatsAppConnectionId)
    {
        if (phone is null)
        {
            return (null, null);
        }

        if (phone.Length is 0 or > PhoneMaxLength || whatsAppConnectionId is null || whatsAppConnectionId == Guid.Empty)
        {
            throw new ArgumentException("A notice phone must be E.164 and come with its WhatsApp connection.", nameof(phone));
        }

        return (phone, whatsAppConnectionId);
    }

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
