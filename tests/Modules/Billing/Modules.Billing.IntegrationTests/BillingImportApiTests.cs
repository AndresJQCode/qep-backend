using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Billing.Application;
using Npgsql;
using static Modules.Billing.IntegrationTests.BillingApiHarness;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// La importación de servicios desde Excel (spec §3.2b y §4 «Importación»), de punta a punta: plantilla, vista previa que no
/// escribe, importación que crea los clientes que faltan, sus perfiles, servicios y cobros, y errores por fila. Los archivos se
/// arman con ClosedXML en la prueba, como en las de Customers.
/// </summary>
public sealed class BillingImportApiTests
{
    private static readonly Guid Tenant = Guid.Parse(BillingApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // America/Bogota (UTC-5, sin horario de verano): la «hoy» del tenant de pruebas.
    private static DateOnly TenantToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private static readonly string[] Headers =
    [
        "Cliente", "Tipo de documento", "Número de documento", "Teléfono", "Correo", "País", "Servicio", "Precio", "Moneda",
        "Ciclo", "Próximo vencimiento", "Canal de aviso", "Encargado",
    ];

    private static string ImportUrl => $"{BillingUrl()}/import";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Una fila en el orden de <see cref="Headers"/>.</summary>
    private static string?[] Row(
        string customer = "Panadería La Esquina",
        string? type = "CC",
        string? number = "1020304050",
        string? phone = "3001234567",
        string? email = "pan@example.com",
        string service = "Hosting",
        string price = "150000",
        string cycle = "Mensual",
        DateOnly? dueDate = null,
        string? channel = "WhatsApp",
        string assignee = "ana@example.com") =>
        [customer, type, number, phone, email, "CO", service, price, "COP", cycle, Iso(dueDate ?? TenantToday.AddMonths(1)), channel, assignee];

    private static byte[] Workbook(params string?[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Cobros");
        for (var column = 0; column < Headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = Headers[column];
        }

        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                sheet.Cell(row + 2, column + 1).Value = rows[row][column] ?? string.Empty;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "cobros.xlsx")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<long> CountAsync(QepApiFactory factory, string sql)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(QepApiFactory factory, string sql)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// Una falla de base que no es de dominio, a pedido: un trigger que rechaza el INSERT en <paramref name="table"/> cuando
    /// <c>name</c> es «Falla». Es lo más cerca de un error real (red, constraint no traducida) sin tocar el código.
    /// </summary>
    private static Task FailInsertsNamedFallaAsync(QepApiFactory factory, string table) => ExecuteAsync(
        factory,
        $"""
        CREATE FUNCTION public.qep_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.name = 'Falla' THEN RAISE EXCEPTION 'forced failure'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER qep_test_fail BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.qep_test_fail();
        """);

    /// <summary>Junta lo que se loguea, para ver qué deja la importación cuando una fila falla.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category, ConcurrentQueue<(string, LogLevel, string, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception), exception));
        }
    }

    private static Task<Guid> SeedAnaAsync(QepApiFactory factory) =>
        SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");

    [Fact]
    public async Task TheTemplateDownloadsWithTheThirteenHeaders()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateManager(factory).GetAsync($"{ImportUrl}/template", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("plantilla-cobros-recurrentes.xlsx", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));
        var sheet = workbook.Worksheets.First();
        Assert.Equal(Headers, Enumerable.Range(1, Headers.Length).Select(column => sheet.Cell(1, column).GetString()));
        Assert.True(sheet.Cell(1, Headers.Length + 1).IsEmpty());
    }

    [Fact]
    public async Task PreviewAnswersValidRowsErrorsAndSummaryWithoutWriting()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedAnaAsync(factory);
        var file = Workbook(
            Row(dueDate: TenantToday.AddMonths(-2)),
            Row(customer: "Otro", number: "555", phone: "3009998877", service: "Dominio", cycle: "Ciclo raro"));

        var preview = await ReadAsync<ImportPreviewDto>(
            await CreateManager(factory).PostAsync($"{ImportUrl}/preview", Upload(file), Ct));

        Assert.Equal("cobros.xlsx", preview.FileName);
        Assert.Equal(2, preview.TotalRows);
        var valid = Assert.Single(preview.ValidRows);
        Assert.Equal(2, valid.RowNumber);
        Assert.Equal("create", valid.Customer.Action);
        Assert.Equal("create", valid.ProfileAction);
        Assert.True(valid.ChargesToGenerate >= 3);
        var error = Assert.Single(preview.Errors);
        Assert.Equal((3, "billing.import.row.cycle_invalid"), (error.RowNumber, error.Code));
        Assert.Equal(1, preview.Summary.CustomersToCreate);
        Assert.Equal(1, preview.Summary.ServicesToCreate);
        Assert.Equal(valid.ChargesToGenerate, preview.Summary.ChargesToGenerate);
        Assert.Equal(0, await CountAsync(factory, "SELECT count(*) FROM billing.services"));
        Assert.Equal(0, await CountAsync(factory, "SELECT count(*) FROM customers.customers"));
    }

    [Fact]
    public async Task ImportCreatesMissingCustomersAsIncompleteAndMatchesExistingOnes()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedAnaAsync(factory);
        var existing = await SeedCustomerAsync(factory, Tenant, "Ferretería Central", "+573110000000", "ferre@example.com");
        var file = Workbook(
            Row(),
            Row(customer: "Ferretería", type: null, number: null, phone: "311 000 0000", email: null, service: "Soporte"));

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(file), Ct));

        Assert.Equal((2, 0, 1), (result.ImportedCount, result.ErrorCount, result.CustomersCreated));
        var created = result.Imported.Single(row => row.RowNumber == 2);
        var matched = result.Imported.Single(row => row.RowNumber == 3);
        Assert.True(created.CustomerCreated);
        Assert.False(matched.CustomerCreated);
        Assert.Equal(existing, matched.CustomerId);
        Assert.Equal(1, await CountAsync(
            factory,
            $"SELECT count(*) FROM customers.customers WHERE id = '{created.CustomerId}' AND cuc IS NULL AND completeness = 'Incomplete' "
            + "AND identification_number = '1020304050' AND email = 'pan@example.com' AND phone_e164 = '+573001234567'"));
        Assert.Equal(1, await CountAsync(
            factory, $"SELECT count(*) FROM audit.entries WHERE action = '{BillingAuditActions.ImportCompleted}'"));
    }

    /// <summary>
    /// §3.2b, «el primero que aplica decide»: la fila trae documento, así que sólo el documento empareja. Que su teléfono sea el
    /// de otro cliente no la suma a ese cliente: es uno nuevo.
    /// </summary>
    [Fact]
    public async Task ANewDocumentWhosePhoneBelongsToAnExistingCustomerCreatesANewCustomer()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedAnaAsync(factory);
        var existing = await SeedCustomerAsync(factory, Tenant, "Ferretería Central", "+573001234567", "ferre@example.com");

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(Workbook(Row())), Ct));

        var imported = Assert.Single(result.Imported);
        Assert.True(imported.CustomerCreated);
        Assert.NotEqual(existing, imported.CustomerId);
        Assert.Equal(1, result.CustomersCreated);
        Assert.Equal(0, await CountAsync(factory, $"SELECT count(*) FROM billing.services WHERE customer_id = '{existing}'"));
    }

    [Fact]
    public async Task ImportCreatesTheProfileTheServiceAndItsCharges()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ana = await SeedAnaAsync(factory);
        var file = Workbook(Row(dueDate: TenantToday.AddMonths(-2), channel: null));

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(file), Ct));

        var imported = Assert.Single(result.Imported);
        Assert.True(imported.ChargesGenerated >= 3);
        Assert.True(imported.OverdueCharges >= 2);
        Assert.Equal(imported.ChargesGenerated, await CountAsync(
            factory, $"SELECT count(*) FROM billing.charges WHERE service_id = '{imported.ServiceId}'"));
        Assert.Equal(imported.OverdueCharges, await CountAsync(
            factory,
            $"SELECT count(*) FROM billing.charges WHERE service_id = '{imported.ServiceId}' AND due_date < '{Iso(TenantToday)}'"));
        Assert.Equal(1, await CountAsync(
            factory,
            $"SELECT count(*) FROM billing.customer_profiles WHERE customer_id = '{imported.CustomerId}' "
            + $"AND channel = 'WhatsApp' AND assignee_member_id = '{ana}'"));
        Assert.Equal(1, await CountAsync(
            factory,
            $"SELECT count(*) FROM billing.services WHERE id = '{imported.ServiceId}' AND name = 'Hosting' AND cycle_months = 1 "
            + $"AND first_due_date = '{Iso(TenantToday.AddMonths(-2))}'"));
    }

    [Fact]
    public async Task ImportKeepsValidRowsWhenOthersFail()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedAnaAsync(factory);
        var file = Workbook(
            Row(),
            Row(service: "Dominio"),
            Row(customer: "Otro", number: "555", phone: "3009998877", price: "gratis"));

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(file), Ct));

        Assert.Equal((3, 2, 1), (result.TotalRows, result.ImportedCount, result.ErrorCount));
        Assert.Equal(1, result.CustomersCreated);
        Assert.Equal(("billing.import.row.price_invalid", 4), (Assert.Single(result.Errors).Code, result.Errors[0].RowNumber));
        Assert.Equal(2, await CountAsync(factory, "SELECT count(*) FROM billing.services"));
        Assert.Equal(1, await CountAsync(factory, "SELECT count(*) FROM billing.customer_profiles"));
    }

    /// <summary>
    /// Una falla que no es de dominio al dar de alta el cliente de una fila: esa fila sale como <c>row.failed</c>, la falla queda
    /// en el log con el número de fila (sin datos personales), y el cliente que no entró no se queda en el contexto de Customers
    /// para romper el alta de las filas siguientes.
    /// </summary>
    [Fact]
    public async Task AFailedCustomerIsLoggedAndTheNextNewCustomersStillImport()
    {
        var logs = new CapturingLoggerProvider();
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), services => services.AddSingleton<ILoggerProvider>(logs));
        await SeedTenantAsync(factory);
        await SeedAnaAsync(factory);
        await FailInsertsNamedFallaAsync(factory, "customers.customers");
        var file = Workbook(
            Row(customer: "Falla", number: "111", phone: "3001111111"),
            Row(customer: "Bien", number: "222", phone: "3002222222"),
            Row(customer: "También", type: null, number: null, phone: "3003333333"));

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(file), Ct));

        var error = Assert.Single(result.Errors);
        Assert.Equal((2, "billing.import.row.failed"), (error.RowNumber, error.Code));
        Assert.Equal([3, 4], result.Imported.Select(row => row.RowNumber));
        Assert.All(result.Imported, row => Assert.True(row.CustomerCreated));
        // Sólo lo de Billing: EF Core también loguea la falla del SaveChanges, con su propia categoría.
        var logged = Assert.Single(
            logs.Entries,
            entry => entry.Category.Contains("Billing", StringComparison.Ordinal)
                && entry.Level == LogLevel.Error
                && entry.Exception is not null);
        Assert.Contains("row 2 ", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Falla", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3001111111", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceCreatedBetweenPreviewAndConfirmBecomesARowError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ana = await SeedAnaAsync(factory);
        var customer = await SeedCustomerAsync(factory, Tenant, "Ferretería Central", "+573110000000", "ferre@example.com");
        var client = CreateManager(factory);
        var file = Workbook(Row(type: null, number: null, phone: "3110000000"));

        var preview = await ReadAsync<ImportPreviewDto>(await client.PostAsync($"{ImportUrl}/preview", Upload(file), Ct));
        Assert.Single(preview.ValidRows);

        var created = await client.PostAsJsonAsync(
            $"{BillingUrl()}/services",
            new
            {
                customerId = customer,
                name = "hosting",
                price = 1000m,
                currency = (string?)null,
                cycle = "Monthly",
                firstDueDate = Iso(TenantToday.AddMonths(1)),
                profile = new { channel = "WhatsApp", assigneeMemberId = ana },
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var result = await ReadAsync<ImportResultDto>(await client.PostAsync(ImportUrl, Upload(file), Ct));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal("billing.import.row.service_exists", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task AnAssigneeWithoutManageIsARowError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedMemberAsync(factory, "asesor@example.com", "Asesor", "advisor");
        var file = Workbook(Row(assignee: "asesor@example.com"));

        var result = await ReadAsync<ImportResultDto>(
            await CreateManager(factory).PostAsync(ImportUrl, Upload(file), Ct));

        Assert.Equal("billing.import.row.assignee_invalid", Assert.Single(result.Errors).Code);
        Assert.Equal(0, await CountAsync(factory, "SELECT count(*) FROM customers.customers"));
    }

    [Fact]
    public async Task MoreThan500RowsAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var rows = Enumerable.Range(0, 501).Select(index => Row(number: $"{100000 + index}")).ToArray();

        var response = await CreateManager(factory).PostAsync($"{ImportUrl}/preview", Upload(Workbook(rows)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.import.too_many_rows", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AFileWithOtherHeadersAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Otra").Cell(1, 1).Value = "Nombre";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var response = await CreateManager(factory).PostAsync(ImportUrl, Upload(stream.ToArray()), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.import.header_mismatch", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithoutManageAnswers403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var reader = CreateReader(factory);

        var preview = await reader.PostAsync($"{ImportUrl}/preview", Upload(Workbook(Row())), Ct);
        var import = await reader.PostAsync(ImportUrl, Upload(Workbook(Row())), Ct);
        var template = await reader.GetAsync($"{ImportUrl}/template", Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            [preview.StatusCode, import.StatusCode, template.StatusCode]);
    }
}
