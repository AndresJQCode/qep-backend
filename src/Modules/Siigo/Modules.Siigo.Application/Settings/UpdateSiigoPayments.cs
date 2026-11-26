using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Una cuenta de cobro de la empresa (por su <see cref="BankAccountKey"/>) y la forma de pago de Siigo que la representa;
/// <c>null</c> es «sin vincular».</summary>
public sealed record SiigoAccountLinkInput(string? BankAccountKey, string? SiigoPaymentTypeId);

/// <summary><c>PUT …/payments</c> (<c>siigo.settings.manage</c>): las cuentas, la forma de pago a crédito con sus días, la de contraentrega
/// y la que usa un pedido sin banco en la foto. Reemplaza el paso entero.</summary>
public sealed record UpdateSiigoPaymentsCommand(
    Guid TenantId,
    Guid ConnectionId,
    IReadOnlyList<SiigoAccountLinkInput>? Accounts,
    string? CreditPaymentTypeId,
    int? CreditDays,
    string? CashOnDeliveryPaymentTypeId,
    string? DefaultPaymentTypeId,
    long ExpectedVersion) : ICommand<SiigoSettingsDto>;

/// <summary>La forma del cuerpo: claves de cuenta sin repetir, y los días de crédito de 1 a 365 y sólo con su forma de pago (el dominio
/// lo exige igual, con su código; acá el error lleva el campo). Una cuenta nula falla en su propio campo.</summary>
public sealed class UpdateSiigoPaymentsValidator : AbstractValidator<UpdateSiigoPaymentsCommand>
{
    public UpdateSiigoPaymentsValidator()
    {
        RuleFor(command => command.Accounts)
            .NotNull()
            .Must(accounts => SiigoSettingsSupport.HasNoRepeated(accounts!, account => account.BankAccountKey))
            .When(command => command.Accounts is not null)
            .WithMessage("A bank account cannot repeat.")
            .OverridePropertyName("accounts");
        RuleForEach(command => command.Accounts).NotNull().ChildRules(account =>
        {
            account.RuleFor(value => value.BankAccountKey).NotEmpty().MaximumLength(BankAccountKey.MaxLength).OverridePropertyName("bankAccountKey");
            account.RuleFor(value => value.SiigoPaymentTypeId).MaximumLength(SiigoTaxLink.TaxIdMaxLength).OverridePropertyName("siigoPaymentTypeId");
        }).OverridePropertyName("accounts");
        RuleFor(command => command.CreditPaymentTypeId).MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("creditPaymentTypeId");
        RuleFor(command => command.CashOnDeliveryPaymentTypeId)
            .MaximumLength(SiigoSettings.IdMaxLength)
            .OverridePropertyName("cashOnDeliveryPaymentTypeId");
        RuleFor(command => command.DefaultPaymentTypeId).MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("defaultPaymentTypeId");
        RuleFor(command => command.CreditDays)
            .InclusiveBetween(1, SiigoSettings.CreditDaysMax)
            .When(command => command.CreditDays is not null)
            .OverridePropertyName("creditDays");
        RuleFor(command => command.CreditPaymentTypeId)
            .NotEmpty()
            .When(command => command.CreditDays is not null)
            .WithMessage("The credit days need their credit payment type.")
            .OverridePropertyName("creditDays");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
    }
}

/// <summary>Spec 2026-11-18 §5.3, paso 5. Mismo orden de comprobaciones que <see cref="AssignSiigoCompanyHandler"/>. Un vínculo de una
/// cuenta que ya no está en la empresa se puede conservar o quitar: la lista que llega es la que queda.</summary>
public sealed class UpdateSiigoPaymentsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoCompanyDirectory companies,
    ISiigoTaxRateDirectory taxRates,
    ISiigoMemberDirectory members,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateSiigoPaymentsCommand> validator) : ICommandHandler<UpdateSiigoPaymentsCommand, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(UpdateSiigoPaymentsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        SiigoSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        settings.UpdatePayments(new SiigoPaymentSettings(
            [.. command.Accounts!.Select(account => new SiigoPaymentLink(account.BankAccountKey!, account.SiigoPaymentTypeId))],
            command.CreditPaymentTypeId,
            command.CreditDays,
            command.CashOnDeliveryPaymentTypeId,
            command.DefaultPaymentTypeId));
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Payments, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }
}
