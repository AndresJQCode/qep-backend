using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Dobles de los puertos que SendQuotationHandler necesita. Registran lo que reciben en vez de
// simularlo: lo que estas pruebas verifican es qué datos salen del handler hacia cada puerto.

/// <summary>Un canal que se cae siempre, para ejercer el camino de falla del envio.</summary>
internal sealed class FailingWhatsAppSender(Exception failure) : IWhatsAppSender
{
    public Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken) =>
        Task.FromException(failure);
}

internal sealed class RecordingQuotationSendFailureLog : IQuotationSendFailureLog
{
    public QuotationHistoryEntry? HistoryEntry { get; private set; }

    public Task RecordAsync(
        QuotationHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        HistoryEntry = historyEntry;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingWhatsAppSender : IWhatsAppSender
{
    public WhatsAppQuotationMessage? Sent { get; private set; }

    public Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        Sent = message;
        return Task.CompletedTask;
    }
}

internal sealed class StubQuotationFileLookup(string downloadUrl) : IQuotationFileLookup
{
    public string? RequestedFileName { get; private set; }

    public Task<QuotationFileRef?> FindAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult<QuotationFileRef?>(
            new QuotationFileRef(fileId, tenantId, "application/pdf", 1024, IsAvailable: true));

    public Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken)
    {
        RequestedFileName = downloadFileName;
        return Task.FromResult(downloadUrl);
    }
}

internal sealed class StubQuotationCustomerLookup(QuotationCustomerRef customer)
    : IQuotationCustomerLookup
{
    /// <summary>Los nombres que resuelve <see cref="FindNamesAsync"/>. Arranca con el cliente
    /// base; una prueba agrega los otros clientes que su listado necesita, y deja afuera al que
    /// quiere ver como "no encontrado".</summary>
    public Dictionary<Guid, string> Names { get; } = new() { [customer.Id] = customer.Name };

    /// <summary>Cuantas veces se pidieron nombres. La razon de ser del puerto batch es que el
    /// listado resuelva todas sus filas en una sola ida, asi que el conteo es la asercion.</summary>
    public int FindNamesCalls { get; private set; }

    public IReadOnlyCollection<Guid> LastRequestedIds { get; private set; } = [];

    public Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult<QuotationCustomerRef?>(customer);

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    /// <summary>Los ids que el filtro por CUC debe resolver. Vacio por defecto: la prueba que
    /// ejerce ese filtro los siembra.</summary>
    public HashSet<Guid> IdsByCuc { get; } = [];

    public string? LastCucTerm { get; private set; }

    public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(
        Guid tenantId, string term, CancellationToken cancellationToken)
    {
        LastCucTerm = term;
        return Task.FromResult<IReadOnlySet<Guid>>(IdsByCuc);
    }

    public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken)
    {
        FindNamesCalls++;
        LastRequestedIds = clientIds;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            clientIds
                .Where(Names.ContainsKey)
                .Distinct()
                .ToDictionary(id => id, id => Names[id]));
    }
}

internal sealed class StubQuotationAdvisorLookup(string? email = null)
    : IQuotationAdvisorLookup
{
    public int FindEmailsCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, string?>> FindEmailsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        FindEmailsCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string?>>(
            membershipIds.Distinct().ToDictionary(id => id, _ => email));
    }
}

internal sealed class StubQuotationRepository(Quotation quotation) : IQuotationRepository
{
    /// <summary>La cotizacion que el doble devuelve, para poder mirar en que estado quedo
    /// despues de que el handler corrio.</summary>
    public Quotation Quotation => quotation;

    /// <summary>Las entradas de historial que el handler agregó, en orden. Un envío y un
    /// reenvío difieren en el tipo de evento y en nada más, así que el historial es lo único
    /// que los distingue después del hecho.</summary>
    public List<QuotationHistoryEntry> HistoryEntries { get; } = [];

    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotation);

    public Task<IReadOnlySet<Guid>> FindIdsWithItemsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(
            quotation.Items.Count > 0 && quotationIds.Contains(quotation.Id)
                ? new HashSet<Guid> { quotation.Id.Value }
                : []);

    public Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<Quotation>, int)>(([quotation], 1));

    public void Add(Quotation quotation)
    {
    }

    /// <summary>El PDF que el caso de uso escribio, o el que ya estaba: se puede sembrar antes
    /// de ejercitar para probar el camino en que no hace falta regenerarlo.</summary>
    public QuotationPdf? Pdf { get; set; }

    public void AddHistoryEntry(QuotationHistoryEntry entry) => HistoryEntries.Add(entry);

    public Task<QuotationPdf?> FindPdfAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(Pdf);

    public void AddPdf(QuotationPdf pdf) => Pdf = pdf;

    public Task<IReadOnlyList<QuotationHistoryEntry>> ListHistoryAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<QuotationHistoryEntry>>([]);
}

internal sealed class NoOpQuotationsUnitOfWork : IQuotationsUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Registra las acciones publicadas. Enviar y reenviar son la misma llamada al canal
/// de WhatsApp: la auditoría es donde queda escrito cuál de las dos fue.</summary>
internal sealed class RecordingQuotationAuditPublisher : IQuotationAuditPublisher
{
    public List<string> Actions { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt) => Actions.Add(action);
}

internal sealed class NoOpQuotationAuditPublisher : IQuotationAuditPublisher
{
    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt)
    {
    }
}

internal sealed class StubMembershipDirectory(Guid membershipId) : IMembershipDirectory
{
    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>([]);

    public Task<Guid?> FindActiveMembershipIdAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(membershipId);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([membershipId]);
}

/// <summary>Concede todo salvo lo que se le nombre: casi toda prueba quiere un sujeto que puede
/// hacer lo que el caso de uso pide, y las que miran un permiso puntual solo tienen que decir
/// cual falta.</summary>
internal sealed class StubExecutionContext(
    Guid subjectId, Guid tenantId, params string[] deniedPermissions) : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => !deniedPermissions.Contains(permission);
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Varias cotizaciones para el listado — <see cref="StubQuotationRepository"/> devuelve
/// siempre una sola, y lo que verifica <c>ListQuotationsHandlerTests</c> es justamente cómo se
/// resuelven los nombres de varias filas (y de filas que repiten cliente) de una sola vez.</summary>
internal sealed class StubQuotationListRepository(params Quotation[] quotations)
    : IQuotationRepository
{
    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotations.FirstOrDefault());

    public Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<Quotation>, int)>((quotations, quotations.Length));

    /// <summary>Contesta desde las cotizaciones sembradas, igual que la consulta real: la fila
    /// del listado necesita saber si hay lineas aunque la busqueda no las traiga.</summary>
    public Task<IReadOnlySet<Guid>> FindIdsWithItemsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(
            quotations
                .Where(quotation =>
                    quotation.Items.Count > 0 && quotationIds.Contains(quotation.Id))
                .Select(quotation => quotation.Id.Value)
                .ToHashSet());

    public void Add(Quotation quotation)
    {
    }

    public void AddHistoryEntry(QuotationHistoryEntry entry)
    {
    }

    public Task<QuotationPdf?> FindPdfAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<QuotationPdf?>(null);

    public void AddPdf(QuotationPdf pdf)
    {
    }

    public Task<IReadOnlyList<QuotationHistoryEntry>> ListHistoryAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<QuotationHistoryEntry>>([]);
}

internal sealed class CountingPdfRenderer : IQuotationPdfRenderer
{
    public int Calls { get; private set; }

    public QuotationPdfDocument? Last { get; private set; }

    public Task<byte[]> RenderAsync(
        QuotationPdfDocument document, CancellationToken cancellationToken)
    {
        Calls++;
        Last = document;
        return Task.FromResult<byte[]>([0x25, 0x50, 0x44, 0x46]);
    }
}

internal sealed class RecordingPdfStorage(string downloadUrl) : IQuotationPdfStorage
{
    public int Saves { get; private set; }

    public string? RequestedFileName { get; private set; }

    public Task<string> SaveAsync(
        Guid tenantId, QuotationId quotationId, byte[] content, CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult($"quotations/tenants/{tenantId:N}/{Saves}.pdf");
    }

    /// <summary>La URL publica que se le entrega a Meta. Distinta de la firmada a proposito:
    /// asi una prueba puede afirmar cual de las dos salio hacia WhatsApp.</summary>
    public const string PublicUrl = "https://assets-qep.example.co/quotations/abc.pdf";

    public string? PublishedKey { get; private set; }

    public Task<string> PublishAsync(string storageKey, CancellationToken cancellationToken)
    {
        PublishedKey = storageKey;
        return Task.FromResult(PublicUrl);
    }

    public Task<string> CreateDownloadUrlAsync(
        string storageKey, string downloadFileName, CancellationToken cancellationToken)
    {
        RequestedFileName = downloadFileName;
        return Task.FromResult(downloadUrl);
    }
}

/// <summary>Compone lo minimo que el mapeo necesita: estas pruebas verifican cuando se
/// regenera, no como se ve el documento -- eso lo cubre QuotationPdfDocumentMapperTests.</summary>
internal sealed class StubQuotationResponseComposer : IQuotationResponseComposer
{
    public Task<QuotationResponse> ComposeAsync(
        Guid tenantId, QuotationDto quotation, CancellationToken cancellationToken) =>
        Task.FromResult(new QuotationResponse(
            quotation.Id,
            quotation.QuotationNumber,
            quotation.ClientId,
            null,
            quotation.AdvisorId,
            null,
            quotation.Status,
            quotation.CreatedAt,
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Currency,
            quotation.Subtotal,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.DiscountAmount,
            quotation.Total,
            quotation.CustomerVatSurplus,
            quotation.RetentionAmount,
            quotation.NetTotal,
            quotation.Notes,
            [],
            quotation.BillingUsesBusinessName,
            quotation.IsStorePickup,
            quotation.BillsToFinalConsumer,
            null,
            quotation.CreatedBy,
            quotation.UpdatedBy,
            quotation.UpdatedAt,
            quotation.SentAt,
            quotation.PdfFileId,
            quotation.CanBeSent,
            quotation.HasChangesSinceSent,
            quotation.CanBeConvertedToSale,
            []));
}

/// <summary>Devuelve las filas sembradas y anota con que filtro se la llamo — lo que las pruebas
/// del listado de ventas necesitan comprobar es el camino del handler, no la consulta SQL.</summary>
internal sealed class StubSaleListRepository(params SaleWithQuotation[] rows) : ISaleRepository
{
    public IReadOnlyCollection<Guid>? LastClientIds { get; private set; }

    public Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Quotation.Id == quotationId)?.Sale);

    public Task<Sale?> FindByIdAsync(
        Guid tenantId, SaleId saleId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Sale.Id == saleId)?.Sale);

    /// <summary>Calcula desde las filas sembradas, igual que la consulta real: agrupa por estado
    /// y suma el total de cada cotizacion mas sus comprobantes. Las dos puntas inclusive.</summary>
    public Task<SaleWindowSummary> SummarizeAsync(
        Guid tenantId,
        DateOnly convertedFrom,
        DateOnly convertedTo,
        CancellationToken cancellationToken)
    {
        var window = rows.Where(row =>
        {
            var day = DateOnly.FromDateTime(row.Sale.ConvertedAt.UtcDateTime);
            return day >= convertedFrom && day <= convertedTo;
        }).ToArray();

        var pending = window.Where(row => row.Sale.Status == SaleStatus.Pending).ToArray();
        var approved = window.Where(row => row.Sale.Status == SaleStatus.Approved).ToArray();

        return Task.FromResult(new SaleWindowSummary(
            pending.Length,
            pending.Sum(row => row.Quotation.Total),
            approved.Length,
            approved.Sum(row => row.Quotation.Total),
            window.Sum(row => row.Sale.PaymentProofs.Sum(proof => proof.Amount))));
    }

    public Task<IReadOnlyDictionary<Guid, Sale>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Sale>>(
            rows
                .Where(row => quotationIds.Contains(row.Quotation.Id))
                .ToDictionary(row => row.Quotation.Id.Value, row => row.Sale));

    public Task<(IReadOnlyList<SaleWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastClientIds = clientIds;
        return Task.FromResult<(IReadOnlyList<SaleWithQuotation>, int)>((rows, rows.Length));
    }

    public void Add(Sale sale) { }
}
