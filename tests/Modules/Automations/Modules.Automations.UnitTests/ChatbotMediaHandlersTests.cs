using BuildingBlocks.Application;
using FluentValidation;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-11-18: subir el archivo de un bloque «Mensaje». La sesión revisa el tipo y el tope de la política de Mensajería (por el
/// puerto) antes de abrir nada en Storage; completar traduce cada estado de Storage a su código, siempre en <c>errors.fileId</c>.
/// </summary>
public sealed class ChatbotMediaHandlersTests
{
    private const long SixteenMegabytes = 16L * 1024 * 1024;
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APngUnderTheCapOpensASession()
    {
        var fixture = new Fixture();

        var session = await fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, "catalogo.png", " IMAGE/PNG ", 1024), Ct);

        Assert.Equal(fixture.Store.Session.FileId, session.FileId);
        Assert.Equal("https://r2.test/staging/abc?upload", session.UploadUrl);
        Assert.Equal("image/png", session.Headers["Content-Type"]);
        // Lo que Storage recibe es el tipo canónico y el tope de la regla, no lo declarado.
        Assert.Equal((TenantId, "catalogo.png", "image/png", 1024L, 25L * 1024 * 1024), Assert.Single(fixture.Store.Opened));
    }

    [Fact]
    public async Task AGifIsUnsupportedOnFileNameBeforeAnyUpload()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<AutomationsDomainException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, "animacion.gif", "image/gif", 1024), Ct));

        Assert.Equal(AutomationsErrorCodes.MediaUnsupportedType, error.Code);
        Assert.Equal([ChatbotMediaMessages.UnsupportedType], error.FieldErrors["fileName"]);
        Assert.Empty(fixture.Store.Opened);
    }

    [Fact]
    public async Task ADeclaredTypeOtherThanTheCanonicalIsUnsupported()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<AutomationsDomainException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, "a.mp3", "audio/mp3", 1024), Ct));

        Assert.Equal(AutomationsErrorCodes.MediaUnsupportedType, error.Code);
        Assert.True(error.FieldErrors.ContainsKey("fileName"));
        Assert.Empty(fixture.Store.Opened);
    }

    [Fact]
    public async Task AVideoOverSixteenMegabytesIsTooLargeOnSizeBytes()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<AutomationsDomainException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, "demo.mp4", "video/mp4", SixteenMegabytes + 1), Ct));

        Assert.Equal(AutomationsErrorCodes.MediaTooLarge, error.Code);
        Assert.Equal([ChatbotMediaMessages.TooLarge], error.FieldErrors["sizeBytes"]);
        Assert.Empty(fixture.Store.Opened);
    }

    [Fact]
    public async Task WithoutChatbotManageItIsForbidden()
    {
        var fixture = new Fixture(permissions: [AutomationsPermissions.ChatbotRead]);

        var session = await Assert.ThrowsAsync<RequestForbiddenException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, "catalogo.png", "image/png", 1024), Ct));
        var complete = await Assert.ThrowsAsync<RequestForbiddenException>(() => fixture.CompleteHandler().HandleAsync(
            new CompleteChatbotMediaCommand(TenantId, fixture.ChatbotId, Guid.CreateVersion7()), Ct));

        Assert.Equal("authorization.denied", session.Code);
        Assert.Equal("authorization.denied", complete.Code);
        Assert.Empty(fixture.Store.Opened);
        Assert.Empty(fixture.Store.Completed);
    }

    [Fact]
    public async Task AnUnknownChatbotIsNotFound()
    {
        var fixture = new Fixture();
        var unknown = Guid.CreateVersion7();

        var session = await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, unknown, "catalogo.png", "image/png", 1024), Ct));
        var complete = await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.CompleteHandler().HandleAsync(
            new CompleteChatbotMediaCommand(TenantId, unknown, Guid.CreateVersion7()), Ct));

        Assert.Equal(AutomationsErrorCodes.ChatbotNotFound, session.Code);
        Assert.Equal(AutomationsErrorCodes.ChatbotNotFound, complete.Code);
        Assert.Empty(fixture.Store.Opened);
        Assert.Empty(fixture.Store.Completed);
    }

    // La forma del cuerpo va antes que la política: un nombre vacío es validation.failed, no un tipo no soportado.
    [Fact]
    public async Task AnEmptyFileNameFailsValidation()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.SessionHandler().HandleAsync(
            new CreateChatbotMediaSessionCommand(TenantId, fixture.ChatbotId, " ", "image/png", 0), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "fileName");
        Assert.Contains(error.Errors, failure => failure.PropertyName == "sizeBytes");
        Assert.Empty(fixture.Store.Opened);
    }

    [Theory]
    [InlineData(ChatbotMediaState.NotFound, AutomationsErrorCodes.MediaNotFound, ChatbotMediaMessages.NotFound)]
    [InlineData(ChatbotMediaState.NotUploaded, AutomationsErrorCodes.MediaNotUploaded, ChatbotMediaMessages.NotUploaded)]
    [InlineData(ChatbotMediaState.Rejected, AutomationsErrorCodes.MediaUnsupportedType, ChatbotMediaMessages.UnsupportedType)]
    public async Task CompleteMapsEachStateToItsCodeOnFileId(ChatbotMediaState state, string code, string message)
    {
        var fixture = new Fixture();
        fixture.Store.Completion = new ChatbotMediaCompletion(state, null);
        var fileId = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<AutomationsDomainException>(() => fixture.CompleteHandler().HandleAsync(
            new CompleteChatbotMediaCommand(TenantId, fixture.ChatbotId, fileId), Ct));

        Assert.Equal(code, error.Code);
        Assert.Equal([message], error.FieldErrors["fileId"]);
        Assert.Equal((TenantId, fileId), Assert.Single(fixture.Store.Completed));
    }

    [Fact]
    public async Task CompleteReturnsTheFile()
    {
        var fixture = new Fixture();
        var fileId = Guid.CreateVersion7();
        fixture.Store.Completion = new ChatbotMediaCompletion(
            ChatbotMediaState.Available,
            new ChatbotMediaFile(fileId, "image", "catalogo.jpg", "image/jpeg", 2048, new Uri($"https://public.test/{fileId}/catalogo.jpg")));

        var file = await fixture.CompleteHandler().HandleAsync(new CompleteChatbotMediaCommand(TenantId, fixture.ChatbotId, fileId), Ct);

        Assert.Equal(
            new ChatbotMediaFileDto(fileId, "image", "catalogo.jpg", "image/jpeg", 2048, $"https://public.test/{fileId}/catalogo.jpg"),
            file);
    }

    private sealed class Fixture
    {
        private readonly Context _context;
        private readonly Chatbots _chatbots;

        public Fixture(IReadOnlyCollection<string>? permissions = null)
        {
            var chatbot = Chatbot.Create(
                TenantId, "Bienvenida", string.Empty, ChatbotTrigger.NewConversation, [],
                """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }""", "{}", Guid.CreateVersion7(),
                DateTimeOffset.UtcNow);
            ChatbotId = chatbot.Id;
            _chatbots = new Chatbots(chatbot);
            _context = new Context(permissions ?? [AutomationsPermissions.ChatbotManage]);
        }

        public Guid ChatbotId { get; }

        public FakeStore Store { get; } = new();

        public CreateChatbotMediaSessionHandler SessionHandler() =>
            new(_context, new AllModules(), _chatbots, new FakePolicy(), Store, new CreateChatbotMediaSessionValidator());

        public CompleteChatbotMediaHandler CompleteHandler() =>
            new(_context, new AllModules(), _chatbots, Store);
    }

    private sealed class Context(IReadOnlyCollection<string> permissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(ChatbotMediaHandlersTests.TenantId);

        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    private sealed class AllModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(TenantModuleKeys.All));
    }

    /// <summary>La columna WhatsApp, recortada a lo que estas pruebas usan.</summary>
    private sealed class FakePolicy : IChatbotMediaPolicy
    {
        public ChatbotMediaRule? RuleFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => new ChatbotMediaRule("image", "image/png", 25L * 1024 * 1024),
            ".mp3" => new ChatbotMediaRule("audio", "audio/mpeg", SixteenMegabytes),
            ".mp4" => new ChatbotMediaRule("video", "video/mp4", SixteenMegabytes),
            _ => null,
        };
    }

    internal sealed class FakeStore : IChatbotMediaStore
    {
        public ChatbotMediaSession Session { get; } = new(
            Guid.CreateVersion7(),
            new Uri("https://r2.test/staging/abc?upload"),
            new Dictionary<string, string> { ["Content-Type"] = "image/png", ["If-None-Match"] = "*" });

        public ChatbotMediaCompletion Completion { get; set; } = new(ChatbotMediaState.NotFound, null);

        public List<(Guid TenantId, string FileName, string ContentType, long SizeBytes, long MaxBytes)> Opened { get; } = [];

        public List<(Guid TenantId, Guid FileId)> Completed { get; } = [];

        public Task<ChatbotMediaSession> OpenUploadAsync(
            Guid tenantId, string fileName, string contentType, long sizeBytes, long maxBytes, CancellationToken cancellationToken)
        {
            Opened.Add((tenantId, fileName, contentType, sizeBytes, maxBytes));
            return Task.FromResult(Session);
        }

        public Task<ChatbotMediaCompletion> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
        {
            Completed.Add((tenantId, fileId));
            return Task.FromResult(Completion);
        }

        public Task<IReadOnlyList<ChatbotMediaFile>> ResolveAsync(
            Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Chatbots(Chatbot chatbot) : IChatbotRepository
    {
        public Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == chatbot.TenantId && id == chatbot.Id ? chatbot : null);

        public Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChatbotVersionLabel?> FindVersionLabelAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotSummaryRow>> ListSummariesAsync(Guid tenantId, string? search, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotServingConnection>> ListServingConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Remove(Chatbot chatbot) => throw new NotSupportedException();

        public Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
