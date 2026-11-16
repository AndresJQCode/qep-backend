namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>Una sección del PDF de un tenant (spec 2026-11-16 §5.1). <see cref="Section"/> es el nombre de
/// <c>PdfSectionKey</c>; una sección sin fila está apagada y vacía.</summary>
internal sealed class PdfSectionRow
{
    public Guid TenantId { get; set; }

    public string Section { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool InQuotation { get; set; }

    public bool InOrder { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ContentHtml { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
