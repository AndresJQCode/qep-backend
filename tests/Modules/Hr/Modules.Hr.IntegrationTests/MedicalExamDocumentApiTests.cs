using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §7: the PDF of the certificate. Its own upload session and download routes, so that whoever only sees exams never
/// needs the contract permissions that <c>/files/*</c> asks for. The store is the in-memory one of the fixture.
/// </summary>
public sealed class MedicalExamDocumentApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamDocumentApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static readonly object SessionBody = new { fileName = "certificado.pdf", mimeType = "application/pdf", sizeBytes = 2048 };

    private async Task<(HrTenant Tenant, string ExamUrl)> SeedAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var response = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/medical-exams",
            new { type = "Periodic", examDate = "2026-11-10", provider = (string?)null, restrictions = (string?)null, recommendations = (string?)null, restrictionsUntil = (string?)null },
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonOfAsync(response);
        return (tenant, $"{EmployeeUrl(tenant, detail)}/medical-exams/{dto.GetProperty("exams")[0].GetProperty("id").GetGuid()}");
    }

    private static Task<HttpResponseMessage> SetDocumentAsync(HttpClient client, string examUrl, Guid? fileId) =>
        client.PutAsJsonAsync($"{examUrl}/document", new { fileId }, Ct);

    [Fact]
    public async Task UploadSessionRequiresMedicalManage()
    {
        using var tenant = await NewTenantAsync(_fixture);
        using var reader = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.MedicalRead, HrPermissions.ContractRead, HrPermissions.ContractManage);

        var denied = await reader.PostAsJsonAsync($"{tenant.Url}/medical-exams/upload-session", SessionBody, Ct);
        var allowed = await tenant.Hr.PostAsJsonAsync($"{tenant.Url}/medical-exams/upload-session", SessionBody, Ct);
        var invalid = await tenant.Hr.PostAsJsonAsync(
            $"{tenant.Url}/medical-exams/upload-session", new { fileName = "certificado.txt", mimeType = "text/plain", sizeBytes = 10 }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var session = await JsonOfAsync(allowed);
        Assert.NotEqual(Guid.Empty, session.GetProperty("fileId").GetGuid());
        Assert.StartsWith("https://", session.GetProperty("uploadUrl").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("hr.file.invalid", CodeOf(await JsonOfAsync(invalid)));
    }

    [Fact]
    public async Task SetDocumentAttachesAndExamShowsHasDocument()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;
        var fileId = _fixture.Documents.Seed(fileName: "certificado.pdf");

        var response = await SetDocumentAsync(tenant.Hr, examUrl, fileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exam = (await JsonOfAsync(response)).GetProperty("exams")[0];
        Assert.True(exam.GetProperty("hasDocument").GetBoolean());
        Assert.Equal(2, exam.GetProperty("version").GetInt64());

        var download = await tenant.Hr.GetAsync($"{examUrl}/document", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"https://files.test/{fileId}", (await JsonOfAsync(download)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task SetDocumentNullRemovesIt()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;
        await SetDocumentAsync(tenant.Hr, examUrl, _fixture.Documents.Seed());

        var response = await SetDocumentAsync(tenant.Hr, examUrl, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await JsonOfAsync(response)).GetProperty("exams")[0].GetProperty("hasDocument").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Hr.GetAsync($"{examUrl}/document", Ct)).StatusCode);
    }

    [Fact]
    public async Task SetDocumentForeignFileReturns422()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;

        var missing = await SetDocumentAsync(tenant.Hr, examUrl, Guid.NewGuid());
        var rejected = await SetDocumentAsync(tenant.Hr, examUrl, _fixture.Documents.Seed(HrDocumentState.Rejected));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("hr.file.not_found", CodeOf(await JsonOfAsync(missing)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("hr.file.rejected", CodeOf(await JsonOfAsync(rejected)));
        Assert.False((await JsonOfAsync(await tenant.Hr.GetAsync(examUrl[..examUrl.LastIndexOf('/')], Ct)))
            .GetProperty("exams")[0].GetProperty("hasDocument").GetBoolean());
    }

    [Fact]
    public async Task SetDocumentWithoutMedicalManageReturns403()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;
        using var reader = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.MedicalRead);

        var response = await SetDocumentAsync(reader, examUrl, _fixture.Documents.Seed());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentWithoutMedicalReadReturns403()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;
        await SetDocumentAsync(tenant.Hr, examUrl, _fixture.Documents.Seed());
        // Seeing the record and even the contract is not enough: the certificate is health data.
        using var noMedical = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.ContractRead);

        var response = await noMedical.GetAsync($"{examUrl}/document", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentOfAnUnknownExamReturns404()
    {
        var (tenant, examUrl) = await SeedAsync();
        using var _ = tenant;

        var response = await tenant.Hr.GetAsync($"{examUrl[..examUrl.LastIndexOf('/')]}/{Guid.NewGuid()}/document", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hr.medical_exam.not_found", CodeOf(await JsonOfAsync(response)));
    }
}
