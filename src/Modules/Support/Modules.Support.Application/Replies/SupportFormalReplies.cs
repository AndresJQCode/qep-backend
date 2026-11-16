using System.Text;
using BuildingBlocks.Domain;
using FluentValidation;
using FluentValidation.Results;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H1 a H4): la parte formal del cuerpo de <c>POST /tickets/{id}/replies</c>. <c>Decision</c> es la sección 6 del PDF
/// (con saltos de línea), <c>SignerTitle</c> el cargo de quien firma, escrito al enviar, y <c>Annexes</c> la lista de anexos impresa
/// (ningún archivo viaja con ella).</summary>
public sealed record SupportFormalReplyRequest(string? Decision, string? SignerTitle, IReadOnlyList<string>? Annexes);

/// <summary>Las reglas de <see cref="SupportFormalReplyRequest"/>, iguales a las del dominio (<see cref="SupportReplyDocument"/>): así
/// ningún cuerpo llega a <c>Create</c> a lanzar un <c>ArgumentException</c> (un 500). Cada error va en su campo
/// (<c>Formal.Decision</c>, <c>Formal.SignerTitle</c>, <c>Formal.Annexes[i]</c>).</summary>
public sealed class SupportFormalReplyRequestValidator : AbstractValidator<SupportFormalReplyRequest>
{
    public SupportFormalReplyRequestValidator()
    {
        RuleFor(formal => formal.Decision)
            .Must(text => Fits(text, SupportReplyDocument.DecisionMaxLength, lineBreaks: true))
            .WithMessage($"Escribe el análisis y la decisión, de 1 a {SupportReplyDocument.DecisionMaxLength} caracteres.");
        RuleFor(formal => formal.SignerTitle)
            .Must(text => Fits(text, SupportReplyDocument.SignerTitleMaxLength, lineBreaks: false))
            .WithMessage($"Escribe el cargo de quien firma, de 1 a {SupportReplyDocument.SignerTitleMaxLength} caracteres en una línea.");
        RuleFor(formal => formal.Annexes)
            .Must(annexes => annexes is null || annexes.Count <= SupportReplyDocument.MaxAnnexes)
            .WithMessage($"Puedes listar hasta {SupportReplyDocument.MaxAnnexes} anexos.");
        RuleForEach(formal => formal.Annexes)
            .Must(annex => Fits(annex, SupportReplyDocument.AnnexMaxLength, lineBreaks: false))
            .WithMessage($"Cada anexo tiene de 1 a {SupportReplyDocument.AnnexMaxLength} caracteres en una línea.");
    }

    /// <summary>Recortado, de 1 a <paramref name="maxLength"/>, sin caracteres de control; con <paramref name="lineBreaks"/>, salvo salto de
    /// línea, retorno y tabulación (los que el PDF imprime).</summary>
    private static bool Fits(string? text, int maxLength, bool lineBreaks)
    {
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= maxLength
            && !trimmed.Any(character => char.IsControl(character) && !(lineBreaks && character is '\n' or '\r' or '\t'));
    }
}

/// <summary>El documento de una respuesta formal ya generado y guardado en el bucket, antes de crear la fila (H7): lo que la fila del
/// documento guarda y los bytes que salen por el canal.</summary>
internal sealed record SupportPreparedFormalReply(
    string Decision,
    string? NextStep,
    Guid SignerMemberId,
    string SignerName,
    string SignerTitle,
    IReadOnlyList<string> Annexes,
    string StorageKey,
    string FileName,
    byte[] Content,
    DateOnly RespondedOn);

/// <summary>
/// Fase 4b, Tarea 8: arma el PDF de una respuesta formal (spec §8.3). En orden: quien firma es el responsable del ticket (H3; sin él,
/// <c>support.reply.formal_signer_required</c>), el membrete es la empresa del formulario (H5; sin ella,
/// <c>support.reply.formal_organization_required</c>), el pie de WhatsApp cabe (H2), el «siguiente paso» es el del tipo o el del tenant (H6),
/// el PDF se genera (H12; si falla, <c>support.reply.document_render_failed</c>) y se guarda en el bucket privado (H8). Nada de esto escribe
/// en la base: la fila del documento la crea quien envía, en el mismo guardado que la respuesta.
/// </summary>
public sealed class SupportFormalReplies(
    ISupportMemberDirectory members,
    ISupportLetterhead letterhead,
    ISupportTicketTypeRepository types,
    ISupportSettingsRepository settings,
    ISupportCustomerLookup customers,
    ITenantDirectory tenants,
    ISupportFormalReplyRenderer renderer,
    ISupportDocumentStorage storage)
{
    /// <summary>El destinatario de un ticket anónimo en el PDF: sin documento ni correo.</summary>
    public const string AnonymousRecipient = "Solicitante anónimo";

    /// <summary>
    /// Revisión del Bloque A: el nombre visible del responsable es una foto de su membresía, que no pasó por las reglas del documento. Sin
    /// caracteres de control, con los espacios juntos colapsados, recortado y cortado a <see cref="SupportReplyDocument.SignerNameMaxLength"/>
    /// (con «…»). Uno que no se resuelve o queda vacío sale «Sin nombre», como en el detalle.
    /// </summary>
    public static string SignerName(string? displayName)
    {
        var builder = new StringBuilder();
        foreach (var character in displayName ?? string.Empty)
        {
            var next = char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character;
            if (next != ' ' || (builder.Length > 0 && builder[^1] != ' '))
            {
                builder.Append(next);
            }
        }

        var clean = builder.ToString().Trim();
        return clean.Length == 0
            ? SupportTicketDtoComposer.UnknownName
            : SnapshotText.Fit(clean, SupportReplyDocument.SignerNameMaxLength);
    }

    internal async Task<SupportPreparedFormalReply> PrepareAsync(
        SupportTicket ticket,
        SupportReplyDelivery delivery,
        string text,
        SupportFormalReplyRequest formal,
        SupportReadContext context,
        CancellationToken cancellationToken)
    {
        if (ticket.AssigneeMemberId is not { } signer)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyFormalSignerRequired, "The ticket's assignee signs the formal reply, and it has none.");
        }

        var names = await members.NamesAsync(ticket.TenantId, [signer], cancellationToken);
        var signerName = SignerName(names.GetValueOrDefault(signer));
        var head = await letterhead.FindAsync(ticket.TenantId, ticket.FormVersionId, cancellationToken)
            ?? throw new SupportDomainException(
                SupportErrorCodes.ReplyFormalOrganizationRequired, "No support form chose the company that answers.");

        // H2: por WhatsApp el texto del canal es el pie del documento, que Meta corta en 1024. Instagram ya tiene su tope (1000) en la ruta.
        if (delivery is SupportReplyDelivery.Text
            && ticket.ConversationChannel == SupportConversationRef.WhatsApp
            && text.Length > SupportReplyAttachmentRules.CaptionMaxLength)
        {
            throw new ValidationException(
                [new ValidationFailure(
                    nameof(SendTicketReplyCommand.Text),
                    $"Con la respuesta formal, el mensaje puede tener hasta {SupportReplyAttachmentRules.CaptionMaxLength} caracteres.")]);
        }

        var nextStep = (await types.FindAsync(ticket.TenantId, ticket.TypeId, cancellationToken))?.FormalNextStep
            ?? (await settings.GetAsync(ticket.TenantId, cancellationToken)).FormalNextStep;
        var dateFormat = RegionalDateFormat.Pattern(
            (await tenants.GetRegionalSettingsAsync(new TenantId(ticket.TenantId), cancellationToken))?.DateFormat);
        var decision = formal.Decision!.Trim();
        var signerTitle = formal.SignerTitle!.Trim();
        var annexes = (formal.Annexes ?? []).Select(annex => annex.Trim()).ToList();
        var recipient = await RecipientAsync(ticket, cancellationToken);

        var content = await renderer.RenderAsync(
            new SupportFormalReplyDocument(
                head,
                ticket.DisplayNumber,
                ticket.ReceivedOn,
                context.Today,
                ticket.TypeName,
                ticket.TermIsLegal,
                recipient.Name,
                recipient.Document,
                recipient.Email,
                recipient.Address,
                ticket.IsAnonymous,
                ticket.Subject,
                ticket.Description,
                decision,
                nextStep,
                signerName,
                signerTitle,
                annexes,
                dateFormat),
            cancellationToken);
        var key = await storage.SaveAsync(ticket.TenantId, content, cancellationToken);
        return new SupportPreparedFormalReply(
            decision, nextStep, signer, signerName, signerTitle, annexes, key, SupportReplyDocument.FileNameFor(ticket.DisplayNumber),
            content, context.Today);
    }

    private sealed record Recipient(string Name, string? Document, string? Email, string? Address);

    // Anónimo: «Solicitante anónimo», sin documento ni correo (plan Tarea 3). Si no, la foto del solicitante y, sin nombre en ella, el
    // cliente vinculado.
    private async Task<Recipient> RecipientAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.IsAnonymous)
        {
            return new Recipient(AnonymousRecipient, null, null, null);
        }

        var requester = ticket.Requester;
        var name = requester?.DisplayName();
        if (string.IsNullOrWhiteSpace(name)
            && ticket.CustomerId is { } customerId
            && await customers.FindAsync(ticket.TenantId, customerId, cancellationToken) is { } customer)
        {
            name = customer.Name;
        }

        var document = requester is { DocumentType: { } type, DocumentNumber: { } number }
            && !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(number)
                ? $"{type.Trim()} {number.Trim()}"
                : null;
        var address = string.Join(
            ", ", new[] { requester?.Address, requester?.CityName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return new Recipient(
            string.IsNullOrWhiteSpace(name) ? SupportTicketDtoComposer.UnknownName : name,
            document,
            string.IsNullOrWhiteSpace(requester?.Email) ? null : requester.Email.Trim(),
            address.Length == 0 ? null : address);
    }
}
