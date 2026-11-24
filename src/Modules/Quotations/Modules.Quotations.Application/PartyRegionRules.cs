using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// The part of the party region rule that needs Geography (spec 2026-11-23): the domain already rejects a region
/// on a Colombian party (<c>quotations.party.city_lane_invalid</c>), and here the region must exist and belong to the
/// party's country.
///
/// A country without a region catalogue has no rows in <c>geography.regions</c>, so it fails like an unknown region
/// or one of another country, without this module knowing the country catalogue. The same goes for a Colombian
/// department id sent as a region: <c>GET /countries/CO/regions</c> returns departments.
/// </summary>
internal static class PartyRegionRules
{
    public const string RegionInvalid = "quotations.party.region_invalid";

    private const string RegionInvalidMessage = "The region does not exist or does not belong to the country.";

    /// <summary>Checks both parties with one geography call. Billing is skipped when the document bills a final
    /// consumer and shipping when the customer picks up at the store: the aggregate discards those parts.</summary>
    public static async Task EnsureAsync(
        IQuotationGeographyLookup geography, QuotationParties parties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parties);
        var candidates = new List<(string Prefix, QuotationPartyDetails Party)>();
        if (parties.Billing is { } billing && !parties.BillsToFinalConsumer)
        {
            candidates.Add(("Parties.Billing", billing));
        }

        if (parties.Shipping is { } shipping && !parties.IsStorePickup)
        {
            candidates.Add(("Parties.Shipping", shipping));
        }

        await EnsureAsync(geography, candidates, cancellationToken);
    }

    public static Task EnsureAsync(
        IQuotationGeographyLookup geography,
        QuotationPartyDetails? party,
        string fieldPrefix,
        CancellationToken cancellationToken) =>
        party is null
            ? Task.CompletedTask
            : EnsureAsync(geography, [(fieldPrefix, party)], cancellationToken);

    private static async Task EnsureAsync(
        IQuotationGeographyLookup geography,
        List<(string Prefix, QuotationPartyDetails Party)> candidates,
        CancellationToken cancellationToken)
    {
        // A Colombian party with a region is the domain's lane error, not a geography question.
        var withRegion = candidates
            .Where(candidate => !candidate.Party.IsColombian
                && candidate.Party.RegionId is { } id && id != Guid.Empty)
            .ToArray();
        if (withRegion.Length == 0)
        {
            return;
        }

        var found = await geography.FindRegionsAsync(
            [.. withRegion.Select(candidate => candidate.Party.RegionId!.Value).Distinct()], cancellationToken);
        foreach (var (prefix, party) in withRegion)
        {
            var country = party.Country!.Trim().ToUpperInvariant();
            if (!found.TryGetValue(party.RegionId!.Value, out var region)
                || !string.Equals(region.CountryCode, country, StringComparison.Ordinal))
            {
                throw new QuotationsDomainException(
                    RegionInvalid,
                    RegionInvalidMessage,
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [$"{prefix}.RegionId"] = [RegionInvalidMessage]
                    });
            }
        }
    }
}

/// <summary>The region of a party as it leaves the module: the id and the name the screen shows.</summary>
public sealed record QuotationRegionDto(Guid Id, string Name);
