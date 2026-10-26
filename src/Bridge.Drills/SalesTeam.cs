using System.Text.Json.Nodes;
using Bridge.Dataverse;

namespace Bridge.Drills;

/// <summary>People in the CRM: create prospects, request onboarding, and edit details of merchants while
/// the bridge is writing the same rows. Remembers what they last wrote (the CRM-owned truth).</summary>
public sealed class SalesTeam(IReadOnlyList<DataverseClient> people, Random random)
{
    private int _next;

    public Dictionary<string, (int PricingPlan, string Iban)> Expected { get; } = new(StringComparer.Ordinal);
    public List<string> Onboarded { get; } = [];
    public int Edits { get; private set; }
    public int Refused { get; private set; }

    private DataverseClient Someone => people[random.Next(people.Count)];

    public async Task OnboardAsync(int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var n = ++_next;
            var id = $"M-{n:000000}";
            var plan = 771000200 + random.Next(3);
            var iban = Ibans.German(37040044, 1_000_000 + n);
            var mcc = n % 17 == 0 ? "7995" : n % 7 == 0 ? "5993" : new[] { "5812", "5411", "7230", "5732" }[random.Next(4)];
            var person = Someone;
            await person.CreateAsync("accounts", new JsonObject
            {
                ["name"] = $"Händler {n} GmbH",
                ["emailaddress1"] = $"kasse{n}@haendler.example",
                ["mcb_merchantid"] = id,
                ["mcb_legalform"] = 771000100,
                ["mcb_country"] = "DE",
                ["mcb_iban"] = iban,
                ["mcb_mcc"] = mcc,
                ["mcb_pricingplan"] = plan,
            }, ct).ConfigureAwait(false);
            await person.InvokeBoundActionAsync("accounts", RowKey.Alternate(("mcb_merchantid", id)), "mcb_RequestOnboarding", null, ct).ConfigureAwait(false);
            Expected[id] = (plan, iban);
            Onboarded.Add(id);
        }
    }

    /// <summary>Edits a few merchants: a new pricing plan or a new settlement IBAN.</summary>
    public async Task EditAsync(int count, CancellationToken ct)
    {
        for (var i = 0; i < count && Onboarded.Count > 0; i++)
        {
            var id = Onboarded[random.Next(Onboarded.Count)];
            var (plan, iban) = Expected[id];
            var body = new JsonObject();
            if (random.Next(2) == 0) { plan = 771000200 + (plan - 771000200 + 1 + random.Next(2)) % 3; body["mcb_pricingplan"] = plan; }
            else { iban = Ibans.German(50010517, random.Next(1, 999_999_999)); body["mcb_iban"] = iban; }
            try
            {
                await Someone.PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", id)), body, "*", null, ct).ConfigureAwait(false);
                Expected[id] = (plan, iban);
                Edits++;
            }
            catch (DataverseRequestException ex) when (ex.Status is 429 or 503)
            {
                Refused++; // a person got "server busy"; they would try again later
            }
        }
    }
}
