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

    /// <summary>Los ids que el filtro por NIT debe resolver. Vacío por defecto, igual que
    /// <see cref="IdsByCuc"/>: la prueba que ejerce ese filtro los siembra, y el doble no inventa
    /// ninguna regla sobre cómo es un NIT.</summary>
    public HashSet<Guid> IdsByIdentification { get; } = [];

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(IdsByIdentification);

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

    /// <summary>Las fichas completas que <see cref="FindManyAsync"/> resuelve. Arranca con el
    /// cliente base, igual que <see cref="Names"/>; una prueba agrega los otros clientes que su
    /// lote necesita.</summary>
    public Dictionary<Guid, QuotationCustomerRef> Refs { get; } = new() { [customer.Id] = customer };

    public int FindManyCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, QuotationCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken)
    {
        FindManyCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationCustomerRef>>(
            clientIds
                .Where(Refs.ContainsKey)
                .Distinct()
                .ToDictionary(id => id, id => Refs[id]));
    }
}

/// <summary><paramref name="resolves"/> en false simula la membresía que ya no es del tenant: el
/// adaptador real la deja fuera del diccionario, que no es lo mismo que devolverla sin correo ni
/// nombre.</summary>
internal sealed class StubQuotationAdvisorLookup(
    string? email = null, string? displayName = null, bool resolves = true)
    : IQuotationAdvisorLookup
{
    public int FindCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationAdvisor>>(
            membershipIds
                .Where(_ => resolves)
                .Distinct()
                .ToDictionary(id => id, _ => new QuotationAdvisor(email, displayName)));
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

    public Task<Quotation?> FindUntrackedAsync(
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
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<Quotation>, int)>(([quotation], 1));

    public Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Quotation>>(after is null ? [quotation] : []);

    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);

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

    public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IQuotationsTransaction>(new NoOpQuotationsTransaction());
}

/// <summary>Sin base no hay nada que confirmar ni deshacer: las pruebas unitarias que la piden
/// afirman sobre los dobles, no sobre la transacción.</summary>
internal sealed class NoOpQuotationsTransaction : IQuotationsTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

/// <summary>Un sujeto del tenant correcto al que le falta el permiso: la mitad de
/// <c>QuotationsAuthorization</c> que <see cref="StubExecutionContext"/> no deja ejercer.</summary>
internal sealed class PermissionlessExecutionContext(Guid subjectId, Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => false;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>El calendario de un tenant en un huso fijo, Bogotá por defecto (spec 2026-09-17). Anota
/// por qué tenant se preguntó: un proceso que recorre tenants pide uno por tenant.</summary>
internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZoneId = "America/Bogota") : ITenantClock
{
    public List<Guid> RequestedTenantIds { get; } = [];

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        RequestedTenantIds.Add(tenantId);
        return Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
    }
}

/// <summary>Los filtros con que se preguntó por filas o se leyó para exportar, ya como instantes:
/// desde inclusivo y antes-de exclusivo (spec 2026-09-17, punto 3).</summary>
internal sealed record RecordedExportSearch(
    Guid? ClientId,
    IReadOnlyCollection<Guid>? ClientIds,
    MemberId? AdvisorId,
    QuotationStatus? Status,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedBefore,
    string? QuotationNumber);

/// <summary>Varias cotizaciones para el listado — <see cref="StubQuotationRepository"/> devuelve
/// siempre una sola, y lo que verifica <c>ListQuotationsHandlerTests</c> es justamente cómo se
/// resuelven los nombres de varias filas (y de filas que repiten cliente) de una sola vez.</summary>
internal sealed class StubQuotationListRepository(params Quotation[] quotations)
    : IQuotationRepository
{
    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotations.FirstOrDefault());

    public Task<Quotation?> FindUntrackedAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotations.FirstOrDefault());

    public Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<Quotation>, int)>((quotations, quotations.Length));

    /// <summary>Cuantas veces se leyo para exportar. Una exportacion rechazada (sin permiso, rango
    /// invalido) no debe llegar a leer nada, y el conteo es la asercion.</summary>
    public int ExportCalls { get; private set; }

    /// <summary>Los filtros con que se pidio la ultima exportacion, para comprobar que el Excel
    /// filtra con lo mismo que el listado.</summary>
    public RecordedExportSearch? LastExportSearch { get; private set; }

    /// <summary>La clave con que se pidió cada lote, en orden: el primero sin clave y cada uno de
    /// los siguientes con la de la última fila del anterior (keyset, D8).</summary>
    public List<QuotationExportCursor?> ExportCursors { get; } = [];

    // Honra el contrato de `clientIds` (vacio = ninguna fila) porque es lo que hace que un NIT
    // sin cliente termine en un Excel vacio, y el orden y la condición del keyset porque es lo que
    // hace que el procesador corte. El resto de los filtros son de la consulta SQL y los cubren
    // las pruebas de integracion.
    public Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        ExportCalls++;
        ExportCursors.Add(after);
        LastExportSearch = new RecordedExportSearch(
            clientId, clientIds, advisorId, status, createdFrom, createdBefore, quotationNumber);
        IReadOnlyList<Quotation> rows = (clientIds is null
                ? quotations
                : quotations.Where(quotation => clientIds.Contains(quotation.ClientId)))
            .Where(quotation => after is null
                || quotation.CreatedAt < after.CreatedAt
                || (quotation.CreatedAt == after.CreatedAt
                    && string.CompareOrdinal(quotation.QuotationNumber, after.QuotationNumber) < 0))
            .OrderByDescending(quotation => quotation.CreatedAt)
            .ThenByDescending(quotation => quotation.QuotationNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(rows);
    }

    /// <summary>Cuántas veces se preguntó si había filas. Un pedido rechazado por permiso o
    /// rango no llega a preguntar, y el conteo es la aserción.</summary>
    public int AnyCalls { get; private set; }

    // Mismo contrato de `clientIds` que ListForExportAsync: un NIT sin cliente es "ninguna fila".
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        CancellationToken cancellationToken)
    {
        AnyCalls++;
        LastExportSearch = new RecordedExportSearch(
            clientId, clientIds, advisorId, status, createdFrom, createdBefore, quotationNumber);
        return Task.FromResult(clientIds is null
            ? quotations.Length > 0
            : quotations.Any(quotation => clientIds.Contains(quotation.ClientId)));
    }

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

/// <summary>El logo que el export ve, mutable entre dos llamados de la misma prueba — así se
/// puede simular que el tenant cambió de logo entre dos exportaciones (spec 2026-09-19, decisión
/// 10).</summary>
internal sealed class StubQuotationLogoLookup : IQuotationLogoLookup
{
    public QuotationLogoRef? Logo { get; set; }

    /// <summary>Los bytes que devuelve <see cref="ReadAsync"/>; null simula un logo que el
    /// adaptador no pudo leer.</summary>
    public byte[]? Content { get; set; } = [0x89, 0x50, 0x4E, 0x47];

    public Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Logo);

    public Task<byte[]?> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken) =>
        Task.FromResult(Content);
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
            quotation.BillingWithRetention,
            quotation.BillingVatSurplus,
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
            quotation.CanBeConvertedToOrder,
            new QuotationMinimumPurchaseResponse(
                quotation.MinimumPurchase.Met,
                quotation.MinimumPurchase.Units,
                quotation.MinimumPurchase.MinimumUnits,
                quotation.MinimumPurchase.MinimumTotal,
                quotation.MinimumPurchase.MissingUnits,
                quotation.MinimumPurchase.MissingTotal),
            [],
            quotation.Version,
            quotation.GlobalScaleFloor,
            quotation.IsRetail,
            // El stub no mira el catalogo, asi que no puede resolver los pisos disponibles. El
            // composer real los llena; esto solo ejerce que el PDF se regenere.
            []));
}

/// <summary>Los filtros con que se preguntó por pedidos o se leyó para exportarlos.</summary>
internal sealed record RecordedOrderExportSearch(
    Guid? ClientId,
    IReadOnlyCollection<Guid>? ClientIds,
    MemberId? AdvisorId,
    OrderStatus? Status,
    OrderPaymentStatus? PaymentStatus,
    DateTimeOffset? ConvertedFrom,
    DateTimeOffset? ConvertedBefore,
    string? OrderNumber);

/// <summary>Devuelve las filas sembradas y anota con que filtro se la llamo — lo que las pruebas
/// del listado de pedidos necesitan comprobar es el camino del handler, no la consulta SQL.</summary>
internal sealed class StubOrderListRepository(params OrderWithQuotation[] rows) : IOrderRepository
{
    public IReadOnlyCollection<Guid>? LastClientIds { get; private set; }

    public int AnyCalls { get; private set; }

    public int ExportCalls { get; private set; }

    /// <summary>La clave con que se pidió cada lote, en orden (keyset, D8).</summary>
    public List<OrderExportCursor?> ExportCursors { get; } = [];

    public RecordedOrderExportSearch? LastExportSearch { get; private set; }

    public Task<Order?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Quotation.Id == quotationId)?.Order);

    public Task<Order?> FindUntrackedByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Order.Id == orderId)?.Order);

    public Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Order.Id == orderId)?.Order);

    public Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Order>>(
            rows
                .Where(row => quotationIds.Contains(row.Quotation.Id))
                .ToDictionary(row => row.Quotation.Id.Value, row => row.Order));

    public Task<(IReadOnlyList<OrderWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        LastClientIds = clientIds;
        return Task.FromResult<(IReadOnlyList<OrderWithQuotation>, int)>((rows, rows.Length));
    }

    // Honra el contrato de `clientIds` (vacío = ninguna fila): es lo que hace que un CUC sin
    // cliente termine en "no hay pedidos". El resto de los filtros son SQL y los cubre integración.
    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        CancellationToken cancellationToken)
    {
        AnyCalls++;
        LastExportSearch = new RecordedOrderExportSearch(
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
        return Task.FromResult(Matching(clientIds).Any());
    }

    public Task<IReadOnlyList<OrderWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        ExportCalls++;
        ExportCursors.Add(after);
        LastExportSearch = new RecordedOrderExportSearch(
            clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
        // Reproduce la forma del keyset en memoria, con comparación ordinal: alcanza para estas
        // pruebas unitarias, aunque el SQL real compara con la collation de la columna. El
        // keyset real contra Postgres lo prueba
        // OrdersCreatedOrLeavingTheFilterBetweenBatchesNeitherRepeatNorSkip.
        IReadOnlyList<OrderWithQuotation> page = Matching(clientIds)
            .Where(row => after is null
                || row.Order.ConvertedAt < after.ConvertedAt
                || (row.Order.ConvertedAt == after.ConvertedAt
                    && string.CompareOrdinal(row.Order.OrderNumber, after.OrderNumber) < 0))
            .OrderByDescending(row => row.Order.ConvertedAt)
            .ThenByDescending(row => row.Order.OrderNumber, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(page);
    }

    /// <summary>Con qué pedidos se pidieron comprobantes, en orden: una vez por lote, no por pedido
    /// (spec 2026-09-15, E6).</summary>
    public List<IReadOnlyCollection<OrderId>> PaymentProofRequests { get; } = [];

    // Los comprobantes que el dominio tiene cargados, en el orden en que se agregaron. El orden por
    // fecha de subida e id es SQL y lo prueba OrderExportApiTests contra Postgres.
    public Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        PaymentProofRequests.Add(orderIds);
        return Task.FromResult<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>>(
            rows
                .Where(row => orderIds.Contains(row.Order.Id) && row.Order.PaymentProofs.Count > 0)
                .ToDictionary(
                    row => row.Order.Id,
                    row => (IReadOnlyList<OrderExportPaymentProof>)row.Order.PaymentProofs
                        .Select(proof => new OrderExportPaymentProof(proof.Id, proof.PublicStorageKey, proof.UploadedAt))
                        .ToArray()));
    }

    // Las líneas y las partes ya viven en el agregado sembrado: a diferencia del repositorio
    // real, acá no hace falta una consulta aparte, sólo leer lo que la prueba cargó con
    // Quotation.AddItemAfterConversion / UpdateDetails.
    public Task<IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationItem>>> ListItemsForExportAsync(
        Guid tenantId, IReadOnlyCollection<QuotationId> quotationIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationItem>>>(
            rows
                .Where(row => quotationIds.Contains(row.Quotation.Id) && row.Quotation.Items.Count > 0)
                .ToDictionary(row => row.Quotation.Id, row => (IReadOnlyList<QuotationItem>)row.Quotation.Items.ToArray()));

    public Task<IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationParty>>> ListPartiesForExportAsync(
        Guid tenantId, IReadOnlyCollection<QuotationId> quotationIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationParty>>>(
            rows
                .Where(row => quotationIds.Contains(row.Quotation.Id) && row.Quotation.Parties.Count > 0)
                .ToDictionary(row => row.Quotation.Id, row => (IReadOnlyList<QuotationParty>)row.Quotation.Parties.ToArray()));

    private IEnumerable<OrderWithQuotation> Matching(IReadOnlyCollection<Guid>? clientIds) =>
        clientIds is null ? rows : rows.Where(row => clientIds.Contains(row.Quotation.ClientId));

    /// <summary>Lo que responde <see cref="IsPaymentProofFileInUseAsync"/> (revisión final, I2).</summary>
    public bool PaymentProofFileInUse { get; set; }

    /// <summary>Cada pregunta de si un archivo ya se usa: el archivo y el comprobante excluido.</summary>
    public List<(Guid FileId, OrderPaymentProofId? ExceptProofId)> PaymentProofFileQuestions { get; } = [];

    public Task<bool> IsPaymentProofFileInUseAsync(
        Guid fileId, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken)
    {
        PaymentProofFileQuestions.Add((fileId, exceptProofId));
        return Task.FromResult(PaymentProofFileInUse);
    }

    public void Add(Order order) { }
}

/// <summary>
/// El publicador de comprobantes (spec 2026-09-15): anota qué se publicó y qué se borró, y con qué
/// token. La clave sale del id del archivo para que la prueba la pueda predecir; la real es
/// aleatoria. Con <c>enabled</c> en false se porta como la opción apagada.
/// </summary>
internal sealed class RecordingPaymentProofPublisher(bool enabled = true) : IPaymentProofPublisher
{
    public const string BaseUrl = "https://assets-qep.example.co";

    private int _publishCalls;

    /// <summary>La llamada a <see cref="PublishAsync"/> (desde 1) que falla; null si ninguna.</summary>
    public int? FailingPublishCall { get; set; }

    /// <summary>La clave cuyo borrado falla, para probar que el rollback sigue con las demás.</summary>
    public string? FailingDeleteKey { get; set; }

    public List<string> PublishedKeys { get; } = [];

    public List<string> DeletedKeys { get; } = [];

    public List<CancellationToken> DeleteTokens { get; } = [];

    public static string KeyFor(Guid fileId) => $"payment-proofs/{fileId:N}.pdf";

    public Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        _publishCalls++;
        if (_publishCalls == FailingPublishCall)
        {
            return Task.FromException<string?>(new InvalidOperationException("Simulated copy failure."));
        }

        if (!enabled)
        {
            return Task.FromResult<string?>(null);
        }

        var key = KeyFor(fileId);
        PublishedKeys.Add(key);
        return Task.FromResult<string?>(key);
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        DeleteTokens.Add(cancellationToken);
        if (publicKey == FailingDeleteKey)
        {
            return Task.FromException(new InvalidOperationException("Simulated delete failure."));
        }

        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public string? UrlFor(string publicKey) => enabled ? $"{BaseUrl}/{publicKey}" : null;
}

/// <summary>Dobles del Excel de pedidos con ERP (ajuste 2026-09-20): producto, empresa y ciudad de
/// una parte, resueltos en lote como los reales, pero desde un diccionario sembrado a mano.</summary>
internal sealed class StubQuotationProductLookup(
    IReadOnlyDictionary<Guid, QuotationProductRef> products) : IQuotationProductLookup
{
    public int FindManyCalls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, QuotationProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        FindManyCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductRef>>(
            productIds.Where(products.ContainsKey).Distinct().ToDictionary(id => id, id => products[id]));
    }
}

internal sealed class StubQuotationCompanyLookup(
    IReadOnlyDictionary<Guid, QuotationCompanyRef> companies) : IQuotationCompanyLookup
{
    public Task<QuotationCompanyRef?> FindAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(companies.GetValueOrDefault(companyId));
}

internal sealed class StubQuotationGeographyLookup(
    IReadOnlyDictionary<Guid, string> cityNames) : IQuotationGeographyLookup
{
    public Task<IReadOnlyDictionary<Guid, string>> FindCityNamesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            cityIds.Where(cityNames.ContainsKey).Distinct().ToDictionary(id => id, id => cityNames[id]));
}
