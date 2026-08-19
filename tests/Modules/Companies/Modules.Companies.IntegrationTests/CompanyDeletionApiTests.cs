using System.Globalization;
using System.Net;
using Modules.Companies.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Companies.IntegrationTests.CompaniesApiHarness;

namespace Modules.Companies.IntegrationTests;

/// <summary>
/// Borrado de empresa.
///
/// La operación que este archivo verifica no es «borrar»: es «borrar **si nadie la referencia**».
/// Hoy ningún módulo apunta a una empresa —`Quotes` no existe todavía—, así que la mitad
/// interesante del contrato no se puede provocar por HTTP. Se provoca por SQL: la prueba crea una
/// tabla con una clave foránea contra `companies.companies(id)`, que es exactamente lo que va a
/// traer el primer módulo que referencie una empresa, y verifica que la violación salga como 422
/// con código de dominio y no como 500.
/// </summary>
public sealed class CompanyDeletionApiTests
{
    [Fact]
    public async Task DeletingACompanyRemovesItFromTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await DeleteAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verificado en base y no por el listado: un filtro mal escrito la escondería del GET
        // dejando la fila donde estaba.
        Assert.Equal(0, await CountCompanyAsync(database, created.Id));
    }

    /// <summary>
    /// Las cuentas bancarias son una colección owned: se van con la empresa. Si quedaran, la tabla
    /// hija acumularía filas huérfanas que ninguna consulta alcanza — invisibles hasta que alguien
    /// mire el esquema.
    /// </summary>
    [Fact]
    public async Task DeletingACompanyTakesItsBankAccountsWithIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        (await DeleteAsync(client, created.Id)).EnsureSuccessStatusCode();

        Assert.Equal(0, await CountBankAccountsAsync(database, created.Id));
    }

    /// <summary>
    /// El caso que justifica la traducción en <c>CompaniesUnitOfWork</c>.
    ///
    /// Sin ella la violación de la clave foránea sale como **500 `server.unexpected`** y, por el
    /// hallazgo `C` de `CAT-04`, con el nombre de la constraint adentro del mensaje. Lo que se
    /// afirma acá es el código: es lo que el frontend lee para decidir si muestra «la empresa está
    /// en uso» o un error genérico.
    /// </summary>
    [Fact]
    public async Task ACompanyThatAnotherRecordReferencesIsRejectedAndSurvives()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        await ReferenceCompanyFromAnotherTableAsync(database, created.Id);

        var response = await DeleteAsync(client, created.Id);

        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("companies.company.in_use", body, StringComparison.Ordinal);
        Assert.Equal(1, await CountCompanyAsync(database, created.Id));
    }

    /// <summary>
    /// Un `DELETE` que devuelva 404 **y borre igual** sería la peor forma de una fuga entre
    /// tenants: la respuesta no deja rastro de lo que hizo. Por eso la aserción que importa no es
    /// el status sino el conteo en base.
    /// </summary>
    [Fact]
    public async Task ACompanyFromAnotherTenantIsNotFoundAndIsNotDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateManager(factory);
        using var other = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage);
        var foreign = await CreateCompanyAsync(
            other, "Empresa Ajena S.A.S.", "CTA-000999", tenantId: OtherTenantId);

        var response = await DeleteAsync(owner, foreign.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await CountCompanyAsync(database, foreign.Id));
    }

    [Fact]
    public async Task DeletingAnUnknownCompanyIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await DeleteAsync(client, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Borrar es administrar: no estrena permiso propio, pero tampoco lo alcanza el de lectura.
    [Fact]
    public async Task DeletingWithOnlyTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CompaniesPermissions.CompanyRead);
        var created = await CreateCompanyAsync(manager, "Andes Logistica S.A.S.", "CTA-000123");

        var response = await DeleteAsync(reader, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await CountCompanyAsync(database, created.Id));
    }

    // Desactivar y borrar son operaciones distintas: la segunda no depende de la primera ni la
    // excluye. Una empresa inactiva se borra igual — el PUT es el único verbo que exige actividad.
    [Fact]
    public async Task AnInactiveCompanyIsStillDeletable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        (await client.PostAsync(
            $"{CompaniesUrl()}/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await DeleteAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountCompanyAsync(database, created.Id));
    }

    // La auditoría y el borrado, en la misma transacción: el evento viaja por el outbox del propio
    // módulo, así que un DELETE que falle no puede dejar registrado un borrado que no ocurrió.
    [Fact]
    public async Task DeletingWritesExactlyOneAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");

        (await DeleteAsync(client, created.Id)).EnsureSuccessStatusCode();

        Assert.Equal(1, await CountAuditEventsAsync(database, "companies.company.deleted"));
    }

    [Fact]
    public async Task ARejectedDeleteWritesNoAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreateCompanyAsync(client, "Andes Logistica S.A.S.", "CTA-000123");
        await ReferenceCompanyFromAnotherTableAsync(database, created.Id);

        var response = await DeleteAsync(client, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountAuditEventsAsync(database, "companies.company.deleted"));
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid companyId) =>
        client.DeleteAsync(
            $"{CompaniesUrl()}/{companyId}",
            TestContext.Current.CancellationToken);

    private static async Task<NpgsqlConnection> OpenAsync(PostgreSqlContainer database)
    {
        var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<int> CountCompanyAsync(PostgreSqlContainer database, Guid companyId)
    {
        await using var connection = await OpenAsync(database);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM companies.companies WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", companyId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountBankAccountsAsync(
        PostgreSqlContainer database,
        Guid companyId)
    {
        await using var connection = await OpenAsync(database);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM companies.company_bank_accounts WHERE company_id = @id",
            connection);
        command.Parameters.AddWithValue("id", companyId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountAuditEventsAsync(
        PostgreSqlContainer database,
        string action)
    {
        await using var connection = await OpenAsync(database);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Crea la referencia que hoy ningún módulo crea todavía.
    ///
    /// La tabla es de la prueba, no del esquema: lo que se ejercita no es «una cotización apunta a
    /// la empresa» sino la clave foránea, que es lo único que ve <c>CompaniesUnitOfWork</c>. Sin
    /// <c>ON DELETE CASCADE</c> a propósito — el default de PostgreSQL (<c>NO ACTION</c>) es el
    /// que frena el borrado, y es el que va a tener cualquier módulo que no diga lo contrario.
    /// </summary>
    private static async Task ReferenceCompanyFromAnotherTableAsync(
        PostgreSqlContainer database,
        Guid companyId)
    {
        await using var connection = await OpenAsync(database);
        await using var create = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS companies.company_references (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES companies.companies (id)
            )
            """,
            connection);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO companies.company_references (id, company_id) VALUES (@id, @companyId)",
            connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("companyId", companyId);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
