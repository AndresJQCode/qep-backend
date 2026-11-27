using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Referencias» y «Pruebas», composición): Storage pregunta a
/// <see cref="IHrDocumentReferences"/> qué PDF siguen en uso antes de purgarlos. Resuelve el adaptador del host real y lo corre contra el
/// esquema migrado con un PDF distinto en cada una de las cinco tablas de Hr que nombran un <c>document_file_id</c>. Si una tabla faltara,
/// Storage purgaría un documento laboral vivo: no hay forma de recuperarlo.
/// </summary>
public sealed class HrDocumentReferencesTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public HrDocumentReferencesTests(HrApiFixture fixture) => _fixture = fixture;

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {response.StatusCode}: {await BodyAsync(response)}");
        return await JsonOfAsync(response);
    }

    [Fact]
    public void TheHostResolvesTheBootstrapperAdapter()
    {
        using var scope = _fixture.Factory.Services.CreateScope();

        var references = scope.ServiceProvider.GetRequiredService<IHrDocumentReferences>();

        Assert.Equal("Bootstrapper.HrDocumentReferences", references.GetType().FullName);
    }

    [Fact]
    public async Task EachOfTheFiveTablesKeepsItsFile()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var contractFile = _fixture.Documents.Seed();
        var salaryFile = _fixture.Documents.Seed();
        var assignmentFile = _fixture.Documents.Seed();
        var renewalFile = _fixture.Documents.Seed();
        var examFile = _fixture.Documents.Seed();

        var detail = await HireAsync(tenant, HireBody(setup, documentFileId: contractFile));
        var contract = detail.GetProperty("contract");
        var salaryId = contract.GetProperty("salaries")[0].GetProperty("id").GetGuid();
        var assignmentId = contract.GetProperty("assignments")[0].GetProperty("id").GetGuid();
        await OkAsync(await tenant.Hr.PutAsJsonAsync($"{ContractUrl(tenant, detail)}/salaries/{salaryId}/document", new { fileId = salaryFile }, Ct));
        await OkAsync(await tenant.Hr.PutAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/assignments/{assignmentId}/document", new { fileId = assignmentFile }, Ct));
        await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/renewals", new { newEndDate = "2027-12-14", documentFileId = renewalFile }, Ct));
        var exams = await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/medical-exams",
            new { type = "Periodic", examDate = "2026-11-10", provider = (string?)null, restrictions = (string?)null, recommendations = (string?)null, restrictionsUntil = (string?)null },
            Ct));
        var examId = exams.GetProperty("exams")[0].GetProperty("id").GetGuid();
        await OkAsync(await tenant.Hr.PutAsJsonAsync($"{EmployeeUrl(tenant, detail)}/medical-exams/{examId}/document", new { fileId = examFile }, Ct));

        Guid[] used = [contractFile, salaryFile, assignmentFile, renewalFile, examFile];
        Guid[] asked = [.. used, Guid.NewGuid()];
        using var scope = _fixture.Factory.Services.CreateScope();
        var references = scope.ServiceProvider.GetRequiredService<IHrDocumentReferences>();

        var found = await references.FindReferencedAsync(tenant.Id, asked, Ct);
        var foreign = await references.FindReferencedAsync(Guid.NewGuid(), asked, Ct);
        var none = await references.FindReferencedAsync(tenant.Id, [], Ct);

        Assert.Equal(used.OrderBy(id => id), found.OrderBy(id => id));
        Assert.Empty(foreign);
        Assert.Empty(none);
    }

    [Fact]
    public async Task TheFiveIndexesExist()
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'hr' AND indexname LIKE 'IX_%_document_file_id' ORDER BY indexname",
            connection);
        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "IX_assignment_records_document_file_id",
                "IX_contract_renewals_document_file_id",
                "IX_employment_contracts_document_file_id",
                "IX_medical_exams_document_file_id",
                "IX_salary_records_document_file_id",
            ],
            names);
    }
}
