using Modules.Billing.Application;
using Modules.Billing.Domain;
using static Modules.Billing.UnitTests.BillingImportTestDoubles;

namespace Modules.Billing.UnitTests;

/// <summary>
/// El plan de la importación (§3.2b) sin base: de las filas válidas, lo que el emparejamiento encontró, los perfiles y servicios
/// que ya existen y los encargados posibles, qué hace cada fila. Hoy del tenant: 2026-03-10.
/// </summary>
public sealed class BillingImportPlannerTests
{
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static readonly BillingMemberRef Ana = new(Guid.NewGuid(), "Ana", "ana@example.com", true, true);

    private static BillingImportContext Context(
        IReadOnlyDictionary<int, IReadOnlyList<BillingCustomerRef>>? matches = null,
        IReadOnlyCollection<Guid>? withProfile = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? openServices = null) => new(
        Today,
        Now,
        "COP",
        matches ?? new Dictionary<int, IReadOnlyList<BillingCustomerRef>>(),
        withProfile ?? [],
        openServices ?? new Dictionary<Guid, IReadOnlyCollection<string>>(),
        new Dictionary<string, BillingMemberRef> { ["ana@example.com"] = Ana });

    private static BillingCustomerRef Customer(string? phone = "+573001234567", string? email = "pan@example.com", bool active = true) =>
        new(Guid.NewGuid(), "Panadería La Esquina", phone, email, active);

    [Fact]
    public void ARowWithoutAMatchCreatesTheCustomerAndTheProfile()
    {
        var plan = BillingImportPlanner.Plan([ImportRow()], Context());

        Assert.Empty(plan.Errors);
        var row = Assert.Single(plan.Rows);
        var preview = row.ToPreview();
        Assert.Equal("create", preview.Customer.Action);
        Assert.Null(preview.Customer.MatchedId);
        Assert.Equal("create", preview.ProfileAction);
        Assert.Equal(Ana.MemberId, row.AssigneeMemberId);
        Assert.Equal("COP", preview.Service.Currency);
        Assert.Equal("Monthly", preview.Service.Cycle);
    }

    [Fact]
    public void ARowMatchingACustomerWithAProfileKeepsIt()
    {
        var customer = Customer();
        var row = ImportRow(assigneeEmail: "nadie@example.com");

        var plan = BillingImportPlanner.Plan(
            [row], Context(matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [row.RowNumber] = [customer] }, withProfile: [customer.Id]));

        Assert.Empty(plan.Errors);
        var preview = Assert.Single(plan.Rows).ToPreview();
        Assert.Equal("match", preview.Customer.Action);
        Assert.Equal(customer.Id, preview.Customer.MatchedId);
        Assert.Equal("kept", preview.ProfileAction);
    }

    [Fact]
    public void ARowMatchingTwoCustomersByPhoneIsAmbiguous()
    {
        var row = ImportRow(identificationType: null, identificationNumber: null);

        var plan = BillingImportPlanner.Plan(
            [row], Context(matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [row.RowNumber] = [Customer(), Customer()] }));

        Assert.Empty(plan.Rows);
        Assert.Equal("billing.import.row.customer_ambiguous", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void AnInactiveMatchIsARowError()
    {
        var row = ImportRow();

        var plan = BillingImportPlanner.Plan(
            [row], Context(matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [row.RowNumber] = [Customer(active: false)] }));

        Assert.Equal("billing.import.row.customer_inactive", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void APastDueDateCountsTheOverdueCharges()
    {
        var plan = BillingImportPlanner.Plan([ImportRow(firstDueDate: "2025-12-10")], Context());

        var preview = Assert.Single(plan.Rows).ToPreview();
        Assert.Equal(4, preview.ChargesToGenerate);
        Assert.Equal(3, preview.OverdueCharges);
    }

    [Fact]
    public void AnExistingActiveServiceWithTheSameNameIsARowError()
    {
        var customer = Customer();
        var row = ImportRow(serviceName: "  HOSTING ");

        var plan = BillingImportPlanner.Plan(
            [row],
            Context(
                matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [row.RowNumber] = [customer] },
                withProfile: [customer.Id],
                openServices: new Dictionary<Guid, IReadOnlyCollection<string>> { [customer.Id] = ["Hosting"] }));

        Assert.Empty(plan.Rows);
        Assert.Equal("billing.import.row.service_exists", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void AnEmptyChannelDefaultsToWhatsAppWithPhoneAndEmailWithout()
    {
        var withPhone = ImportRow(rowNumber: 2);
        var customerWithoutPhone = Customer(phone: null);
        var withoutPhone = ImportRow(rowNumber: 3, identificationNumber: "999", phoneE164: null);

        var plan = BillingImportPlanner.Plan(
            [withPhone, withoutPhone],
            Context(matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [3] = [customerWithoutPhone] }));

        Assert.Empty(plan.Errors);
        Assert.Equal(BillingChannel.WhatsApp, plan.Rows[0].Channel);
        Assert.Equal(BillingChannel.Email, plan.Rows[1].Channel);
    }

    [Fact]
    public void ARowThatCreatesTheCustomerNeedsAPhone()
    {
        var plan = BillingImportPlanner.Plan([ImportRow(phoneE164: null, channel: BillingChannel.Email)], Context());

        Assert.Equal("billing.import.row.phone_required", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void AnUnknownAssigneeIsARowErrorWhenTheProfileIsCreated()
    {
        var plan = BillingImportPlanner.Plan([ImportRow(assigneeEmail: "nadie@example.com")], Context());

        Assert.Equal("billing.import.row.assignee_invalid", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void AMatchedCustomerWithoutTheChannelContactIsARowError()
    {
        var customer = Customer(email: null);
        var row = ImportRow(channel: BillingChannel.Email);

        var plan = BillingImportPlanner.Plan(
            [row], Context(matches: new Dictionary<int, IReadOnlyList<BillingCustomerRef>> { [row.RowNumber] = [customer] }));

        Assert.Equal("billing.import.row.email_required", Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void TwoServicesOfTheSameNewCustomerCreateItOnceAndTheProfileOnce()
    {
        var plan = BillingImportPlanner.Plan(
            [ImportRow(rowNumber: 2, serviceName: "Hosting"), ImportRow(rowNumber: 3, serviceName: "Dominio")], Context());

        Assert.Equal(["create", "kept"], plan.Rows.Select(row => row.ToPreview().ProfileAction));
        Assert.Equal(1, plan.Summary.CustomersToCreate);
        Assert.Equal(2, plan.Summary.ServicesToCreate);
    }

    /// <summary>
    /// La segunda fila dice «kept», pero lleva el canal y el encargado del perfil que planeó la primera: si la escritura de esa
    /// fila falla, la importación vuelve a mirar y crea el perfil con ellos en vez de dejar el servicio sin perfil.
    /// </summary>
    [Fact]
    public void EveryRowOfAProfilelessCustomerCarriesTheProfileToCreate()
    {
        var plan = BillingImportPlanner.Plan(
            [ImportRow(rowNumber: 2, serviceName: "Hosting", channel: BillingChannel.Email), ImportRow(rowNumber: 3, serviceName: "Dominio")],
            Context());

        Assert.Equal(
            new (BillingChannel?, Guid?)[] { (BillingChannel.Email, Ana.MemberId), (BillingChannel.Email, Ana.MemberId) },
            plan.Rows.Select(row => (row.Channel, row.AssigneeMemberId)).ToArray());
    }
}
