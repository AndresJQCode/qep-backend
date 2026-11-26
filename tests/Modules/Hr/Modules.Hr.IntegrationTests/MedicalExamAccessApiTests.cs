using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Hr.Application;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §6 (anti-objective): restrictions and recommendations are health data, so seeing the employee record is not
/// enough. Every medical endpoint needs <c>hr.employee.read</c> and <c>hr.medical.read</c>; writing needs <c>hr.medical.manage</c>
/// as well.
/// </summary>
public sealed class MedicalExamAccessApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamAccessApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static readonly object Body = new
    {
        type = "Periodic",
        examDate = "2026-11-10",
        provider = (string?)null,
        restrictions = (string?)null,
        recommendations = (string?)null,
        restrictionsUntil = (string?)null,
    };

    private async Task<(HrTenant Tenant, string Url, Guid ExamId, string Version, SeededSetup Setup)> SeedAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var url = $"{EmployeeUrl(tenant, detail)}/medical-exams";
        var response = await tenant.Hr.PostAsJsonAsync(url, Body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonOfAsync(response);
        return (tenant, url, dto.GetProperty("exams")[0].GetProperty("id").GetGuid(), "\"1\"", setup);
    }

    private static async Task<HttpStatusCode[]> EveryEndpointAsync(HttpClient client, string url, Guid examId, string version)
    {
        var codes = new List<HttpStatusCode>
        {
            (await client.GetAsync(url, Ct)).StatusCode,
            (await client.PostAsJsonAsync(url, Body, Ct)).StatusCode,
            (await PutAsync(client, $"{url}/{examId}", Body, version)).StatusCode,
            (await client.PostAsJsonAsync(
                url.Replace("/medical-exams", "/exit-exam/not-attended"), new { notifiedOn = "2026-11-17", notes = (string?)null }, Ct)).StatusCode,
        };
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{url}/{examId}");
        delete.Headers.TryAddWithoutValidation("If-Match", version);
        codes.Add((await client.SendAsync(delete, Ct)).StatusCode);
        return [.. codes];
    }

    [Fact]
    public async Task GetWithEmployeeReadButNoMedicalReadReturns403()
    {
        var (tenant, url, examId, version, _) = await SeedAsync();
        using var _ = tenant;
        using var client = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead, HrPermissions.ContractManage);

        Assert.All(await EveryEndpointAsync(client, url, examId, version), code => Assert.Equal(HttpStatusCode.Forbidden, code));
    }

    [Fact]
    public async Task WritesWithMedicalReadButNoMedicalManageReturn403AndTheReadWorks()
    {
        var (tenant, url, examId, version, _) = await SeedAsync();
        using var _ = tenant;
        using var client = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.MedicalRead);

        var codes = await EveryEndpointAsync(client, url, examId, version);

        Assert.Equal(HttpStatusCode.OK, codes[0]);
        Assert.All(codes.Skip(1), code => Assert.Equal(HttpStatusCode.Forbidden, code));
    }

    [Fact]
    public async Task MedicalPermissionsWithoutEmployeeReadReturn403()
    {
        var (tenant, url, examId, version, _) = await SeedAsync();
        using var _ = tenant;
        using var client = tenant.Client(HrPermissions.MedicalRead, HrPermissions.MedicalManage);

        Assert.All(await EveryEndpointAsync(client, url, examId, version), code => Assert.Equal(HttpStatusCode.Forbidden, code));
    }

    [Fact]
    public async Task ADifferentActiveTenantReturns403NeverTheExamOfAnother()
    {
        var (tenant, url, examId, version, _) = await SeedAsync();
        using var _ = tenant;
        using var other = await NewTenantAsync(_fixture);

        // The caller works in `other` and asks for the route of `tenant`.
        Assert.All(await EveryEndpointAsync(other.Hr, url, examId, version), code => Assert.Equal(HttpStatusCode.Forbidden, code));
    }

    [Fact]
    public async Task AnExamOfAnotherEmployeeIsNotFoundThroughThisOne()
    {
        var (tenant, _, examId, _, setup) = await SeedAsync();
        using var _ = tenant;
        var second = await HireAsync(tenant, HireBody(setup, documentNumber: "1099887766"));

        var response = await PutAsync(tenant.Hr, $"{EmployeeUrl(tenant, second)}/medical-exams/{examId}", Body, "\"1\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hr.medical_exam.not_found", CodeOf(await JsonOfAsync(response)));
    }

}
