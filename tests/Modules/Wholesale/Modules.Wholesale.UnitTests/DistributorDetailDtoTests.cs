using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>
/// <c>InvitationUrl</c> lleva el token plano. El <c>ToString</c> generado de un record imprime todas sus propiedades, así que un
/// log o un mensaje de aserción que lo nombre filtraría el link: el record lo excluye.
/// </summary>
public sealed class DistributorDetailDtoTests
{
    private const string Url = "https://qep.qcode.co/wholesale-invitations/abc_DEF-secreto";

    [Fact]
    public void ToStringNeverPrintsTheInvitationUrl()
    {
        var id = Guid.CreateVersion7();
        var dto = new DistributorDetailDto(
            id,
            "Invited",
            3,
            "Distribuciones Norte SAS",
            "CO",
            "NIT",
            "900123456",
            "Ana Pérez",
            "ana@distri.co",
            null,
            new DistributorPriceListDto(Guid.CreateVersion7(), "Oro", 10m),
            new DistributorAdvisorDto(Guid.CreateVersion7(), "Ana Mayorista"),
            new DistributorCompanyDto(Guid.CreateVersion7(), "Mayorista Central SAS"),
            null,
            null,
            new DistributorInvitationDto(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, null),
            DateTimeOffset.UnixEpoch,
            null,
            [],
            Url);

        var text = dto.ToString();

        Assert.DoesNotContain("abc_DEF-secreto", text, StringComparison.Ordinal);
        Assert.DoesNotContain("wholesale-invitations", text, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), text, StringComparison.Ordinal);
    }
}
