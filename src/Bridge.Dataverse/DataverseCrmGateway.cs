using System.Globalization;
using System.Text.Json.Nodes;
using Bridge.Core.Domain;
using Bridge.Core.Ports;

namespace Bridge.Dataverse;

/// <summary>
/// <see cref="ICrmGateway"/> over the Dataverse Web API. The account is addressed by its alternate key
/// (mcb_merchantid); the merchant change, the terminal upsert and the inbound-event ledger row go in one
/// $batch changeset; the ledger row is created with If-None-Match:* so a second delivery cannot insert it
/// again, and the account is patched with If-Match on the version read, so a concurrent writer is caught.
/// </summary>
public sealed class DataverseCrmGateway(DataverseClient client, TimeProvider clock) : ICrmGateway
{
    public const string AccountColumns =
        "accountid,name,emailaddress1,mcb_merchantid,mcb_lifecyclestage,mcb_legalform,mcb_country,mcb_iban,mcb_mcc,mcb_pricingplan,mcb_crmversion," +
        "mcb_platformaccountid,mcb_kycdecision,mcb_kycreason,mcb_kycversion,mcb_platformstatus,mcb_statusreason,mcb_statusversion," +
        "mcb_chargebackratio,mcb_riskband,mcb_riskversion,mcb_volumemonth,mcb_lastmonthvolume,mcb_lastmonthtransactions,mcb_volumeversion," +
        "mcb_platformversion,mcb_lastsyncedon";

    public async Task<CrmMerchant?> GetMerchantAsync(string merchantId, CancellationToken ct)
    {
        var (row, etag) = await Call(() => client.GetAsync("accounts", RowKey.Alternate(("mcb_merchantid", merchantId)), AccountColumns, ct)).ConfigureAwait(false);
        return row is null ? null : ToMerchant(row, etag!);
    }

    public static CrmMerchant ToMerchant(JsonObject row, string etag) => new()
    {
        AccountId = Guid.Parse((string)row["accountid"]!),
        MerchantId = (string)row["mcb_merchantid"]!,
        ETag = etag,
        Stage = Choices.StageOf((int?)row["mcb_lifecyclestage"]),
        CrmVersion = (long?)row["mcb_crmversion"] ?? 0,
        LegalName = (string?)row["name"],
        ContactEmail = (string?)row["emailaddress1"],
        LegalForm = Choices.Name(Choices.LegalForm, (int?)row["mcb_legalform"]),
        Country = (string?)row["mcb_country"],
        Iban = (string?)row["mcb_iban"],
        Mcc = (string?)row["mcb_mcc"],
        PricingPlan = Choices.Name(Choices.PricingPlan, (int?)row["mcb_pricingplan"]),
        PlatformAccountId = (string?)row["mcb_platformaccountid"],
        KycDecision = Choices.Name(Choices.KycDecision, (int?)row["mcb_kycdecision"]),
        KycReason = (string?)row["mcb_kycreason"],
        KycVersion = (long?)row["mcb_kycversion"] ?? 0,
        PlatformStatus = Choices.Name(Choices.PlatformStatus, (int?)row["mcb_platformstatus"]),
        StatusReason = (string?)row["mcb_statusreason"],
        StatusVersion = (long?)row["mcb_statusversion"] ?? 0,
        ChargebackRatio = (decimal?)row["mcb_chargebackratio"],
        RiskBand = Choices.Name(Choices.RiskBand, (int?)row["mcb_riskband"]),
        RiskVersion = (long?)row["mcb_riskversion"] ?? 0,
        VolumeMonth = (string?)row["mcb_volumemonth"],
        LastMonthVolume = (decimal?)row["mcb_lastmonthvolume"],
        LastMonthTransactions = (int?)row["mcb_lastmonthtransactions"],
        VolumeVersion = (long?)row["mcb_volumeversion"] ?? 0,
        PlatformVersion = (long?)row["mcb_platformversion"] ?? 0,
        LastSyncedOn = row["mcb_lastsyncedon"] is { } d ? DateTimeOffset.Parse((string)d!, CultureInfo.InvariantCulture) : null,
    };

    public async Task<bool> InboundEventSeenAsync(string eventId, CancellationToken ct)
    {
        var (row, _) = await Call(() => client.GetAsync("mcb_inboundevents", RowKey.Alternate(("mcb_eventid", eventId)), "mcb_inboundeventid", ct)).ConfigureAwait(false);
        return row is not null;
    }

    public async Task CommitAsync(CrmMerchant read, Effect effect, InboundRecord ledger, CancellationToken ct)
    {
        var ledgerBody = new JsonObject
        {
            ["mcb_name"] = $"{ledger.EventType} {ledger.MerchantId} #{ledger.Sequence}",
            ["mcb_eventtype"] = ledger.EventType,
            ["mcb_merchantid"] = ledger.MerchantId,
            ["mcb_sequence"] = ledger.Sequence,
            ["mcb_outcome"] = Choices.InboundOutcome[ledger.Outcome switch
            {
                Outcome.Applied => "applied",
                Outcome.Stale => "stale",
                _ => "rejected",
            }],
            ["mcb_detail"] = ledger.Detail,
            ["mcb_processedon"] = Iso(ledger.ProcessedOn),
        };
        var ledgerKey = RowKey.Alternate(("mcb_eventid", ledger.EventId));
        var createOnly = new Dictionary<string, string> { ["If-None-Match"] = "*" };

        try
        {
            if (!effect.WritesMerchant && effect.Terminal is null)
            {
                await client.PatchAsync("mcb_inboundevents", ledgerKey, ledgerBody, ifMatch: null, ifNoneMatch: "*", ct).ConfigureAwait(false);
                return;
            }
            var ops = new List<BatchOperation>();
            if (effect.WritesMerchant)
                ops.Add(new BatchOperation("PATCH", $"accounts({read.AccountId})", PatchBody(effect.Patch), new Dictionary<string, string> { ["If-Match"] = read.ETag }));
            if (effect.Terminal is { } t)
            {
                ops.Add(new BatchOperation("PATCH", $"mcb_terminals({RowKey.Alternate(("mcb_terminalref", t.TerminalId)).Segment})", new JsonObject
                {
                    ["mcb_name"] = $"{t.TerminalId} ({t.MerchantId})",
                    ["mcb_merchantid"] = t.MerchantId,
                    ["mcb_model"] = t.Model,
                    ["mcb_activatedon"] = Iso(t.ActivatedAt),
                }));
            }
            ops.Add(new BatchOperation("PATCH", $"mcb_inboundevents({ledgerKey.Segment})", ledgerBody, createOnly));
            await client.ChangesetAsync(ops, ct).ConfigureAwait(false);
        }
        catch (DataverseRequestException ex) when (ex.Status == 412)
        {
            if (ex.Code == DataverseRequestException.ConcurrencyMismatch) throw new ConcurrencyException(ex.Detail, ex);
            if (ex.Code == DataverseRequestException.DuplicateKey) throw new DuplicateEventException(ex.Detail, ex);
            // An unknown 412: ask the ledger which it was.
            if (await InboundEventSeenAsync(ledger.EventId, ct).ConfigureAwait(false)) throw new DuplicateEventException(ex.Detail, ex);
            throw new ConcurrencyException(ex.Detail, ex);
        }
        catch (DataverseRequestException ex) when (ex.IsTransient)
        {
            throw new TransientException(ex.Message, ex.RetryAfter, ex);
        }
    }

    public static JsonObject PatchBody(MerchantPatch patch)
    {
        var body = new JsonObject();
        foreach (var (field, value) in patch.Values)
        {
            switch (field)
            {
                case PlatformField.Stage: body["mcb_lifecyclestage"] = Choices.Stage[(LifecycleStage)value!]; break;
                case PlatformField.PlatformAccountId: body["mcb_platformaccountid"] = (string?)value; break;
                case PlatformField.KycDecision: body["mcb_kycdecision"] = Choices.Value(Choices.KycDecision, (string?)value); break;
                case PlatformField.KycReason: body["mcb_kycreason"] = (string?)value; break;
                case PlatformField.KycVersion: body["mcb_kycversion"] = (long)value!; break;
                case PlatformField.PlatformStatus: body["mcb_platformstatus"] = Choices.Value(Choices.PlatformStatus, (string?)value); break;
                case PlatformField.StatusReason: body["mcb_statusreason"] = (string?)value; break;
                case PlatformField.StatusVersion: body["mcb_statusversion"] = (long)value!; break;
                case PlatformField.ChargebackRatio: body["mcb_chargebackratio"] = (decimal?)value; break;
                case PlatformField.RiskBand: body["mcb_riskband"] = Choices.Value(Choices.RiskBand, (string?)value); break;
                case PlatformField.RiskVersion: body["mcb_riskversion"] = (long)value!; break;
                case PlatformField.VolumeMonth: body["mcb_volumemonth"] = (string?)value; break;
                case PlatformField.LastMonthVolume: body["mcb_lastmonthvolume"] = (decimal?)value; break;
                case PlatformField.LastMonthTransactions: body["mcb_lastmonthtransactions"] = (int?)value; break;
                case PlatformField.VolumeVersion: body["mcb_volumeversion"] = (long)value!; break;
                case PlatformField.PlatformVersion: body["mcb_platformversion"] = (long)value!; break;
                case PlatformField.LastSyncedOn: body["mcb_lastsyncedon"] = Iso((DateTimeOffset)value!); break;
                default: throw new ArgumentOutOfRangeException(nameof(patch), field, "no column for this field");
            }
        }
        return body;
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(int max, CancellationToken ct)
    {
        var page = await Call(() => client.QueryAsync("mcb_outboxmessages", $"mcb_status eq {Choices.OutboxStatus["pending"]}",
            "mcb_outboxmessageid,mcb_eventid,mcb_eventtype,mcb_merchantid,mcb_sequence,mcb_schemaversion,mcb_payload,mcb_attempts,createdon",
            "createdon asc,mcb_sequence asc", max, max, ct)).ConfigureAwait(false);
        return page.Rows.Select(r => new OutboxMessage(
            Guid.Parse((string)r["mcb_outboxmessageid"]!),
            (string)r["@odata.etag"]!,
            (string)r["mcb_eventid"]!,
            (string)r["mcb_eventtype"]!,
            (string)r["mcb_merchantid"]!,
            (long?)r["mcb_sequence"] ?? 0,
            (int?)r["mcb_schemaversion"] ?? 1,
            (string?)r["mcb_payload"] ?? "{}",
            DateTimeOffset.Parse((string)r["createdon"]!, CultureInfo.InvariantCulture),
            (int?)r["mcb_attempts"] ?? 0)).ToList();
    }

    public Task MarkOutboxSentAsync(OutboxMessage message, DateTimeOffset at, CancellationToken ct) =>
        Call(() => client.PatchAsync("mcb_outboxmessages", RowKey.Id(message.RowId), new JsonObject
        {
            ["mcb_status"] = Choices.OutboxStatus["sent"],
            ["mcb_publishedon"] = Iso(at),
            ["mcb_attempts"] = message.Attempts + 1,
        }, ifMatch: "*", ifNoneMatch: null, ct));

    public Task MarkOutboxFailedAsync(OutboxMessage message, string error, CancellationToken ct) =>
        Call(() => client.PatchAsync("mcb_outboxmessages", RowKey.Id(message.RowId), new JsonObject
        {
            ["mcb_attempts"] = message.Attempts + 1,
            ["mcb_lasterror"] = error.Length > 400 ? error[..400] : error,
        }, ifMatch: "*", ifNoneMatch: null, ct));

    public async Task<CrmPage<CrmMerchant>> ListSyncedMerchantsAsync(string? continuation, int pageSize, CancellationToken ct)
    {
        var page = continuation is null
            ? await Call(() => client.QueryAsync("accounts", $"mcb_lifecyclestage ne {Choices.Stage[LifecycleStage.Prospect]} and mcb_merchantid ne null",
                AccountColumns, "mcb_merchantid asc", null, pageSize, ct)).ConfigureAwait(false)
            : await Call(() => client.QueryUrlAsync(continuation, pageSize, ct)).ConfigureAwait(false);
        return new CrmPage<CrmMerchant>(page.Rows.Select(r => ToMerchant(r, (string)r["@odata.etag"]!)).ToList(), page.NextLink);
    }

    public Task WriteDriftAsync(DriftFinding finding, CancellationToken ct) =>
        Call(() => client.CreateAsync("mcb_driftfindings", new JsonObject
        {
            ["mcb_name"] = $"{finding.MerchantId}: {finding.Field}",
            ["mcb_merchantid"] = finding.MerchantId,
            ["mcb_field"] = finding.Field,
            ["mcb_crmvalue"] = finding.CrmValue,
            ["mcb_platformvalue"] = finding.PlatformValue,
            ["mcb_resolution"] = Choices.DriftResolution[finding.Healed ? "healed" : "needs_review"],
            ["mcb_detectedon"] = Iso(finding.DetectedOn),
        }, ct));

    public async Task<bool> TryAcquireLeaseAsync(string name, string holder, TimeSpan duration, CancellationToken ct)
    {
        var key = RowKey.Alternate(("mcb_name", name));
        var now = clock.GetUtcNow();
        var body = new JsonObject { ["mcb_holder"] = holder, ["mcb_expireson"] = Iso(now + duration) };
        try
        {
            var (row, etag) = await client.GetAsync("mcb_bridgeleases", key, "mcb_holder,mcb_expireson", ct).ConfigureAwait(false);
            if (row is null)
            {
                await client.PatchAsync("mcb_bridgeleases", key, body, ifMatch: null, ifNoneMatch: "*", ct).ConfigureAwait(false);
                return true;
            }
            var expires = row["mcb_expireson"] is { } e ? DateTimeOffset.Parse((string)e!, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
            if ((string?)row["mcb_holder"] != holder && expires > now) return false;
            await client.PatchAsync("mcb_bridgeleases", key, body, ifMatch: etag, ifNoneMatch: null, ct).ConfigureAwait(false);
            return true;
        }
        catch (DataverseRequestException ex) when (ex.Status == 412)
        {
            return false; // someone else took or renewed it first
        }
        catch (DataverseRequestException ex) when (ex.IsTransient)
        {
            throw new TransientException(ex.Message, ex.RetryAfter, ex);
        }
    }

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<T> Call<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DataverseRequestException ex) when (ex.IsTransient)
        {
            throw new TransientException(ex.Message, ex.RetryAfter, ex);
        }
    }

    private static async Task Call(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DataverseRequestException ex) when (ex.IsTransient)
        {
            throw new TransientException(ex.Message, ex.RetryAfter, ex);
        }
    }
}
