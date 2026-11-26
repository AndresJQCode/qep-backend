using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Los PDF de Hr por HTTP (spec 2026-11-20, «Archivos (PDF)»): la sesión de subida y la URL firmada de descarga, con el puerto de
/// Storage reemplazado por <see cref="FakeHrDocumentStore"/>.
/// </summary>
public sealed class HrFilesApiTests : IClassFixture<HrFilesApiTests.World>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public HrFilesApiTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal FakeHrDocumentStore Store { get; } = new();

        internal QepApiFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            var store = Store;
            Factory = new QepApiFactory(_database.GetConnectionString(), services =>
            {
                services.RemoveAll<IHrDocumentStore>();
                services.AddSingleton<IHrDocumentStore>(store);
            });
            await SeedTenantAsync(Factory);
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    private static string FilesUrl(string path) => $"{HrUrl()}/files/{path}";

    private HttpClient Client(params string[] permissions) => CreateClient(_world.Factory, SubjectId, TenantId, permissions);

    private static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task TheUploadSessionAnswersTheSignedPut()
    {
        using var client = Client(HrPermissions.ContractRead, HrPermissions.ContractManage);

        var response = await client.PostAsJsonAsync(
            FilesUrl("upload-session"), new { fileName = "contrato.pdf", mimeType = "application/pdf", sizeBytes = 2048 }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        var fileId = body.GetProperty("fileId").GetGuid();
        Assert.StartsWith("https://r2.test/staging/", body.GetProperty("uploadUrl").GetString());
        Assert.Equal("application/pdf", body.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.NotEqual(Guid.Empty, fileId);
        Assert.Contains(_world.Store.Opens, open => open is { FileName: "contrato.pdf", MimeType: "application/pdf", SizeBytes: 2048 });
    }

    [Theory]
    [InlineData("contrato.png", "image/png", 2048)]
    [InlineData("contrato.pdf", "image/png", 2048)]
    [InlineData("contrato.docx", "application/pdf", 2048)]
    [InlineData("contrato.pdf", "application/pdf", 10_485_761)]
    [InlineData("contrato.pdf", "application/pdf", 0)]
    [InlineData("   ", "application/pdf", 2048)]
    public async Task ANonPdfOrALargerFileIsFileInvalid(string fileName, string mimeType, long sizeBytes)
    {
        using var client = Client(HrPermissions.ContractRead, HrPermissions.ContractManage);
        var before = _world.Store.Opens.Count;

        var response = await client.PostAsJsonAsync(FilesUrl("upload-session"), new { fileName, mimeType, sizeBytes }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.file.invalid", (await JsonOfAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, _world.Store.Opens.Count);
    }

    [Fact]
    public async Task WithoutContractManageTheSessionIs403()
    {
        using var client = Client(HrPermissions.ContractRead);

        var response = await client.PostAsJsonAsync(
            FilesUrl("upload-session"), new { fileName = "contrato.pdf", mimeType = "application/pdf", sizeBytes = 2048 }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheDownloadAnswersTheSignedUrl()
    {
        var fileId = _world.Store.Seed(HrDocumentState.Available);
        using var client = Client(HrPermissions.ContractRead);

        var response = await client.GetAsync(FilesUrl(fileId.ToString()), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://files.test/{fileId}", (await JsonOfAsync(response)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task AnUnknownFileIs404FileNotFound()
    {
        using var client = Client(HrPermissions.ContractRead);

        var response = await client.GetAsync(FilesUrl(Guid.NewGuid().ToString()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hr.file.not_found", (await JsonOfAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WithoutContractReadTheDownloadIs403()
    {
        var fileId = _world.Store.Seed(HrDocumentState.Available);
        using var client = Client(HrPermissions.EmployeeRead);

        var response = await client.GetAsync(FilesUrl(fileId.ToString()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
