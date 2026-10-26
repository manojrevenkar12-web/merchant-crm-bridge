using Bridge.Core.Contracts;
using Bridge.Core.Domain;

namespace Bridge.Tests;

public class MapperTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly EventReader Reader = EventReader.Default;

    private static CrmMerchant Merchant(LifecycleStage stage = LifecycleStage.Onboarding) =>
        new() { AccountId = Account, MerchantId = "M-000001", ETag = "W/\"1\"", Stage = stage };

    private static readonly Guid Account = Guid.Parse("2b1f0000-0000-4000-8000-000000000001");

    private static (CrmMerchant After, Effect Effect) Apply(CrmMerchant m, CloudEvent ce)
    {
        var effect = PlatformEventMapper.Decide(m, Reader.Read(ce), ce.Sequence, Now);
        return (FakeCrm.Apply(m, effect.Patch), effect);
    }

    [Fact]
    public void The_stage_follows_the_platform_through_the_lifecycle()
    {
        var m = Merchant();
        (m, _) = Apply(m, Events.Kyc("M-000001", 1, "more_information"));
        Assert.Equal(LifecycleStage.KycPending, m.Stage);
        (m, _) = Apply(m, Events.Kyc("M-000001", 2, "approved"));
        Assert.Equal(LifecycleStage.Approved, m.Stage);
        (m, _) = Apply(m, Events.Boarded("M-000001", 3));
        Assert.Equal(LifecycleStage.Boarded, m.Stage);
        (m, _) = Apply(m, Events.Status("M-000001", 4, "live"));
        Assert.Equal(LifecycleStage.Live, m.Stage);
        (m, _) = Apply(m, Events.Status("M-000001", 5, "restricted"));
        Assert.Equal(LifecycleStage.Restricted, m.Stage);
        Assert.Equal(5, m.PlatformVersion);
        Assert.Equal(Now, m.LastSyncedOn);
    }

    [Fact]
    public void An_older_event_for_an_aspect_is_stale_and_writes_nothing()
    {
        var m = Merchant();
        (m, _) = Apply(m, Events.Risk("M-000001", 7, "high", 0.021m));
        var (after, effect) = Apply(m, Events.Risk("M-000001", 6, "low", 0.001m));
        Assert.Equal(Outcome.Stale, effect.Outcome);
        Assert.True(effect.Patch.IsEmpty);
        Assert.Equal("high", after.RiskBand);
    }

    [Fact]
    public void A_late_event_for_another_aspect_still_applies()
    {
        // risk at sequence 9 arrived before status at sequence 8: the status must not be lost.
        var m = Merchant();
        (m, _) = Apply(m, Events.Risk("M-000001", 9, "medium", 0.004m));
        var (after, effect) = Apply(m, Events.Status("M-000001", 8, "live"));
        Assert.Equal(Outcome.Applied, effect.Outcome);
        Assert.Equal(LifecycleStage.Live, after.Stage);
        Assert.Equal(9, after.PlatformVersion); // the highest sequence seen does not go backwards
    }

    [Fact]
    public void Volume_keeps_the_latest_month_and_accepts_a_newer_restatement()
    {
        var m = Merchant(LifecycleStage.Live);
        (m, _) = Apply(m, Events.VolumeV2("M-000001", 3, "2026-09", 1_250_000, 50_000));
        Assert.Equal(12_000m, m.LastMonthVolume); // gross minus refunds, in euro
        var (afterOld, old) = Apply(m, Events.VolumeV2("M-000001", 4, "2026-08", 999_00));
        Assert.Equal(Outcome.Stale, old.Outcome);
        Assert.Equal("2026-09", afterOld.VolumeMonth);
        (m, _) = Apply(m, Events.VolumeV2("M-000001", 5, "2026-09", 1_300_000));
        Assert.Equal(13_000m, m.LastMonthVolume);
        var (_, usd) = Apply(m, Events.VolumeV2("M-000001", 6, "2026-10", 100, currency: "USD"));
        Assert.Equal(Outcome.Rejected, usd.Outcome);
    }

    [Fact]
    public void Version_1_volume_is_upcast_to_euro_gross_without_refunds()
    {
        var evt = (VolumeReported)Reader.Read(Events.VolumeV1("M-000001", 1, "2026-09", 777_00));
        Assert.Equal(new Money(777_00, "EUR"), evt.Gross);
        Assert.Equal(0, evt.RefundsMinor);
    }

    [Fact]
    public void Terminals_are_facts_applied_whatever_the_order()
    {
        var m = Merchant(LifecycleStage.Live) with { PlatformVersion = 50, StatusVersion = 50, PlatformStatus = "live" };
        var (_, effect) = Apply(m, Events.Terminal("M-000001", 2, "T-77"));
        Assert.Equal(Outcome.Applied, effect.Outcome);
        Assert.Equal("T-77", effect.Terminal!.TerminalId);
    }

    [Fact]
    public void Events_for_a_prospect_or_a_conflicting_account_are_rejected()
    {
        Assert.Equal(Outcome.Rejected, Apply(Merchant(LifecycleStage.Prospect), Events.Kyc("M-000001", 1, "approved")).Effect.Outcome);
        var boarded = Merchant() with { PlatformAccountId = "PA-1" };
        Assert.Equal(Outcome.Rejected, Apply(boarded, Events.Boarded("M-000001", 2, "PA-2")).Effect.Outcome);
        Assert.Equal(Outcome.Stale, Apply(boarded, Events.Boarded("M-000001", 3, "PA-1")).Effect.Outcome);
    }

    /// <summary>The core promise: any order of the same events, delivered any number of times, ends in
    /// the same CRM row.</summary>
    [Fact]
    public void Any_order_and_any_duplicates_converge_to_the_same_row()
    {
        var events = new List<CloudEvent>
        {
            Events.Kyc("M-000001", 1, "more_information"),
            Events.Kyc("M-000001", 2, "approved"),
            Events.Boarded("M-000001", 3),
            Events.Terminal("M-000001", 4, "T-1"),
            Events.Status("M-000001", 5, "live"),
            Events.Risk("M-000001", 6, "medium", 0.004m),
            Events.VolumeV1("M-000001", 7, "2026-08", 500_000),
            Events.VolumeV2("M-000001", 8, "2026-09", 900_000, 10_000),
            Events.Risk("M-000001", 9, "high", 0.019m),
            Events.Status("M-000001", 10, "restricted"),
            Events.Terminal("M-000001", 11, "T-2"),
        };
        var expected = events.Aggregate(Merchant(), (m, e) => Apply(m, e).After);

        var random = new Random(20261026);
        for (var run = 0; run < 500; run++)
        {
            var delivered = events.Concat(events.Where(_ => random.Next(3) == 0)).OrderBy(_ => random.Next()).ToList();
            var actual = delivered.Aggregate(Merchant(), (m, e) => Apply(m, e).After);
            Assert.Equal(expected with { LastSyncedOn = null }, actual with { LastSyncedOn = null });
        }
        Assert.Equal(LifecycleStage.Restricted, expected.Stage);
        Assert.Equal("high", expected.RiskBand);
        Assert.Equal(8_900m, expected.LastMonthVolume);
    }
}
