using System.Security.Cryptography;

namespace Modules.Support.Domain;

/// <summary>
/// Fase 4b (H3, H4, H9): el PDF de la respuesta formal de una respuesta del ticket, una fila por respuesta. Es prueba legal de lo que
/// se envió, así que es insert-only: no tiene mutadores. Guarda lo que el PDF imprime y no se puede volver a calcular (la decisión,
/// el «siguiente paso» vigente al enviar, quién firma y con qué cargo, los anexos y la fecha), dónde quedó el archivo y su hash. Un
/// reintento de la respuesta manda este documento, nunca uno nuevo (H7).
/// </summary>
public sealed class SupportReplyDocument
{
    /// <summary>H2: la sección 6 del PDF, con saltos de línea.</summary>
    public const int DecisionMaxLength = 20_000;

    /// <summary>H3: el cargo de quien firma, texto libre que el asesor escribe al enviar.</summary>
    public const int SignerTitleMaxLength = 120;

    /// <summary>El nombre visible del firmante, una foto tomada al enviar.</summary>
    public const int SignerNameMaxLength = 200;

    /// <summary>H4: los anexos son una lista de texto impresa; ningún archivo viaja con ellos.</summary>
    public const int MaxAnnexes = 10;

    public const int AnnexMaxLength = 200;

    public const int StorageKeyMaxLength = 512;

    public const int FileNameMaxLength = 64;

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private SupportReplyDocument()
    {
        Decision = string.Empty;
        SignerName = string.Empty;
        SignerTitle = string.Empty;
        Annexes = [];
        StorageKey = string.Empty;
        FileName = string.Empty;
        Sha256Hex = string.Empty;
    }

    public Guid ReplyId { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public string Decision { get; private set; }

    /// <summary>H6: el del tipo o, sin él, el del tenant, como estaba al enviar. Null: el PDF omite la sección 7.</summary>
    public string? NextStep { get; private set; }

    /// <summary>H3 (owner 2026-11-16, Q-7): la membresía del responsable del ticket, no la de quien envía. Retiene al usuario en
    /// <c>SupportUserReferenceProbe</c>.</summary>
    public Guid SignerMemberId { get; private set; }

    public string SignerName { get; private set; }

    public string SignerTitle { get; private set; }

    public IReadOnlyList<string> Annexes { get; private set; }

    /// <summary>H8: la clave del objeto en el bucket privado, bajo <c>support/</c> (sin regla de ciclo de vida).</summary>
    public string StorageKey { get; private set; }

    public string FileName { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 del PDF, en hexadecimal en minúsculas.</summary>
    public string Sha256Hex { get; private set; }

    /// <summary>La fecha local del tenant que el PDF imprime como fecha de la respuesta.</summary>
    public DateOnly RespondedOn { get; private set; }

    /// <summary>H10: el <c>clientId</c> del segundo mensaje de Instagram (el archivo después del texto). Se guarda para que un
    /// reintento lo repita y Mensajería, idempotente por él, no lo mande dos veces.</summary>
    public Guid DocumentClientId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>El nombre con que se descarga el PDF: lleva el radicado visible (<c>SOP-000127</c>).</summary>
    public static string FileNameFor(string displayNumber) => $"Respuesta-{displayNumber}.pdf";

    /// <summary>
    /// Los textos llegan validados por el validador de la API; acá se repiten las reglas para que el dominio no dependa de quien llama.
    /// Una respuesta lleva un solo documento (H11): con un adjunto del hilo no se puede.
    /// </summary>
    public static SupportReplyDocument Create(
        SupportTicketReply reply,
        string decision,
        string? nextStep,
        Guid signerMemberId,
        string signerName,
        string signerTitle,
        IReadOnlyList<string> annexes,
        string storageKey,
        string fileName,
        byte[] content,
        DateOnly respondedOn,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(annexes);
        ArgumentNullException.ThrowIfNull(content);

        // Plan Tarea 2: sólo texto, plantilla, correo o página de consulta. Una entrega nueva decide si lleva documento, no lo hereda.
        if (reply.Delivery is not (SupportReplyDelivery.Text or SupportReplyDelivery.Template or SupportReplyDelivery.Email
            or SupportReplyDelivery.Portal))
        {
            throw new ArgumentException($"A '{reply.Delivery}' reply cannot carry a formal reply document.", nameof(reply));
        }

        if (reply.UploadId is not null)
        {
            throw new ArgumentException("A reply with an attachment cannot carry a formal reply document.", nameof(reply));
        }

        if (signerMemberId == Guid.Empty)
        {
            throw new ArgumentException("A formal reply needs its signer.", nameof(signerMemberId));
        }

        if (annexes.Count > MaxAnnexes)
        {
            throw new ArgumentException($"A formal reply has at most {MaxAnnexes} annexes.", nameof(annexes));
        }

        if (content.Length == 0)
        {
            throw new ArgumentException("The document has no content.", nameof(content));
        }

        return new SupportReplyDocument
        {
            ReplyId = reply.Id,
            TenantId = reply.TenantId,
            TicketId = reply.TicketId,
            Decision = Text(decision, DecisionMaxLength, nameof(decision)),
            NextStep = SupportFormalNextStep.Normalize(nextStep),
            SignerMemberId = signerMemberId,
            SignerName = Text(signerName, SignerNameMaxLength, nameof(signerName)),
            SignerTitle = Text(signerTitle, SignerTitleMaxLength, nameof(signerTitle)),
            Annexes = annexes.Select(annex => Text(annex, AnnexMaxLength, nameof(annexes))).ToArray(),
            StorageKey = Text(storageKey, StorageKeyMaxLength, nameof(storageKey)),
            FileName = Text(fileName, FileNameMaxLength, nameof(fileName)),
            SizeBytes = content.LongLength,
            Sha256Hex = Convert.ToHexStringLower(SHA256.HashData(content)),
            RespondedOn = respondedOn,
            DocumentClientId = Guid.CreateVersion7(),
            CreatedAt = now,
        };
    }

    /// <summary>Recortado, entre 1 y <paramref name="maxLength"/> caracteres, sin caracteres de control salvo salto de línea, retorno
    /// y tabulación (el PDF los imprime; el resto no sabe).</summary>
    private static string Text(string? value, int maxLength, string parameter)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > maxLength
            || trimmed.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException(
                $"The value must have between 1 and {maxLength} characters and no control characters.", parameter);
        }

        return trimmed;
    }
}
