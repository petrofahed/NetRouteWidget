namespace NetRoute.Core.Tests;

public class SmartRoutingPolicyTests
{
    static DetectionResult AllAdapters() => DetectionResult.Of(
        TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None,
        TestAdapters.Wifi(), DetectionIssue.None);

    [Theory]
    [InlineData(ConnectionId.Lan, RoutingMode.Lan, RuleTarget.Lan, "lan")]
    [InlineData(ConnectionId.Wifi, RoutingMode.Wifi, RuleTarget.Wifi, "wifi")]
    [InlineData(ConnectionId.Phone, RoutingMode.Phone, RuleTarget.Phone, "phone")]
    public void Connection_identities_and_qualified_carriers_are_preserved(
        ConnectionId id, RoutingMode mode, RuleTarget target, string tag)
    {
        var net = NetworkStatus.Initial(mode, true) with { Adapters = AllAdapters(), Carrier = id };
        Assert.Equal(target, SmartRoutingPolicy.TargetOf(id));
        Assert.Equal(tag, SmartRoutingPolicy.Tag(id));
        Assert.Equal(id, SmartRoutingPolicy.ProfileOf(net));
        Assert.Equal(id, SmartRoutingPolicy.WantedExit(net));
    }

    [Theory]
    [InlineData(RuleTarget.Home, "only-home")]
    [InlineData(RuleTarget.Lan, "only-lan")]
    [InlineData(RuleTarget.Wifi, "only-wifi")]
    [InlineData(RuleTarget.Phone, "only-phone")]
    public void Logical_home_and_literal_targets_have_distinct_selector_tags(RuleTarget target, string expected) =>
        Assert.Equal(expected, SmartRoutingPolicy.SelectorTag(target));

    [Fact]
    public void Auto_and_missing_status_offer_only_a_paused_phone_profile_not_an_exit()
    {
        var auto = NetworkStatus.Initial(RoutingMode.Auto, true) with
        {
            Adapters = AllAdapters(), Carrier = ConnectionId.Lan, HealTarget = ConnectionId.Wifi,
        };
        Assert.Equal(ConnectionId.Phone, SmartRoutingPolicy.ProfileOf(null));
        Assert.Equal(ConnectionId.Phone, SmartRoutingPolicy.ProfileOf(auto));
        Assert.Null(SmartRoutingPolicy.WantedExit(auto));
    }

    [Theory]
    [InlineData(RoutingMode.Phone, ConnectionId.Wifi)]
    [InlineData(RoutingMode.Wifi, ConnectionId.Lan)]
    public void Wanted_exit_follows_the_physical_heal_instead_of_the_tun_path(RoutingMode mode, ConnectionId heal)
    {
        var net = NetworkStatus.Initial(mode, true) with
        {
            Adapters = AllAdapters(), HealTarget = heal, ActivePath = InternetPath.Phone,
        };
        Assert.Equal(heal, SmartRoutingPolicy.WantedExit(net));
    }

    [Theory]
    [InlineData(DetectionIssue.NotFound)]
    [InlineData(DetectionIssue.TurnedOff)]
    [InlineData(DetectionIssue.NotUsed)]
    public void A_carrier_that_is_no_longer_used_or_present_is_recalculated(DetectionIssue issue)
    {
        var adapters = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, null, issue);
        var net = NetworkStatus.Initial(RoutingMode.Phone, true) with
        {
            Adapters = adapters, Carrier = ConnectionId.Wifi, HealTarget = ConnectionId.Lan, ActivePath = InternetPath.Wifi,
        };
        Assert.Equal(ConnectionId.Lan, SmartRoutingPolicy.WantedExit(net));
    }

    [Theory]
    [InlineData(false, "192.168.1.20")]
    [InlineData(true, null)]
    public void A_carrier_without_gateway_or_ipv4_is_recalculated(bool gateway, string? ipv4)
    {
        var adapters = AllAdapters() with
        {
            Slots = new Dictionary<ConnectionId, (AdapterInfo?, DetectionIssue)>(AllAdapters().Slots)
            {
                [ConnectionId.Wifi] = (TestAdapters.Wifi() with { HasGateway = gateway, IPv4 = ipv4 }, DetectionIssue.None),
            },
        };
        var net = NetworkStatus.Initial(RoutingMode.Phone, true) with { Adapters = adapters, Carrier = ConnectionId.Wifi };
        Assert.Equal(ConnectionId.Phone, SmartRoutingPolicy.WantedExit(net));
    }

    [Fact]
    public void Qualified_carrier_wins_over_heal_hint_and_no_eligible_carrier_waits()
    {
        var net = NetworkStatus.Initial(RoutingMode.Phone, true) with
        {
            Adapters = AllAdapters(), Carrier = ConnectionId.Lan, HealTarget = ConnectionId.Wifi, ActivePath = InternetPath.Phone,
        };
        Assert.Equal(ConnectionId.Lan, SmartRoutingPolicy.WantedExit(net));
        Assert.Null(SmartRoutingPolicy.WantedExit(net with { Adapters = DetectionResult.Empty }));
    }

    // Literal expectations follow the approved table, independent of the policy's ordering helpers.
    public static TheoryData<ConnectionId, RuleTarget, ConnectionId[]> Orders => new()
    {
        { ConnectionId.Phone, RuleTarget.Home, [ConnectionId.Lan, ConnectionId.Wifi] },
        { ConnectionId.Lan, RuleTarget.Home, [ConnectionId.Lan, ConnectionId.Wifi] },
        { ConnectionId.Wifi, RuleTarget.Home, [ConnectionId.Wifi, ConnectionId.Lan] },
        { ConnectionId.Phone, RuleTarget.Lan, [ConnectionId.Lan, ConnectionId.Wifi] },
        { ConnectionId.Lan, RuleTarget.Lan, [ConnectionId.Lan, ConnectionId.Wifi] },
        { ConnectionId.Wifi, RuleTarget.Lan, [ConnectionId.Lan, ConnectionId.Wifi] },
        { ConnectionId.Phone, RuleTarget.Wifi, [ConnectionId.Wifi, ConnectionId.Lan] },
        { ConnectionId.Lan, RuleTarget.Wifi, [ConnectionId.Wifi, ConnectionId.Lan] },
        { ConnectionId.Wifi, RuleTarget.Wifi, [ConnectionId.Wifi, ConnectionId.Lan] },
        { ConnectionId.Phone, RuleTarget.Phone, [ConnectionId.Phone] },
        { ConnectionId.Lan, RuleTarget.Phone, [ConnectionId.Phone] },
        { ConnectionId.Wifi, RuleTarget.Phone, [ConnectionId.Phone] },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public void Selector_membership_and_answering_subsets_never_invent_mobile_fallback(
        ConnectionId profile, RuleTarget target, ConnectionId[] expected)
    {
        var adapters = AllAdapters();
        Assert.Equal(expected, SmartRoutingPolicy.Candidates(target, profile, ConnectionId.Phone, adapters, false));
        HashSet<ConnectionId>[] subsets = [[], [ConnectionId.Lan], [ConnectionId.Wifi], [ConnectionId.Phone], [ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone]];
        foreach (var answering in subsets)
        {
            ConnectionId? wanted = expected.Where(answering.Contains).Select(id => (ConnectionId?)id).FirstOrDefault();
            Assert.Equal(wanted, SmartRoutingPolicy.Select(target, profile, ConnectionId.Phone, adapters, answering, false));
        }
        if (target != RuleTarget.Phone)
        {
            Assert.DoesNotContain(ConnectionId.Phone, expected);
            Assert.Equal(ConnectionId.Phone, SmartRoutingPolicy.Select(target, profile, ConnectionId.Phone, adapters, new HashSet<ConnectionId> { ConnectionId.Phone }, true));
        }
    }

    [Theory]
    [InlineData(ConnectionId.Lan)]
    [InlineData(ConnectionId.Wifi)]
    [InlineData(ConnectionId.Phone)]
    public void Explicit_use_main_prefers_the_answering_current_path_and_deduplicates_it(ConnectionId current)
    {
        foreach (var profile in Connections.All)
        foreach (var target in Enum.GetValues<RuleTarget>())
        {
            var candidates = SmartRoutingPolicy.Candidates(target, profile, current, AllAdapters(), true);
            Assert.Single(candidates, id => id == current);
            Assert.Equal(current, SmartRoutingPolicy.Select(target, profile, current, AllAdapters(), new HashSet<ConnectionId>(Connections.All), true));
            Assert.Null(SmartRoutingPolicy.Select(target, profile, current, AllAdapters(), new HashSet<ConnectionId>(), true));
        }
    }

    [Theory]
    [InlineData(ConnectionId.Lan)]
    [InlineData(ConnectionId.Wifi)]
    public void Phone_target_falls_back_to_the_current_path_when_phone_does_not_answer(ConnectionId current)
    {
        Assert.Equal(new[] { ConnectionId.Phone, current }, SmartRoutingPolicy.Candidates(RuleTarget.Phone, ConnectionId.Phone, current, AllAdapters(), false));
        Assert.Equal(current, SmartRoutingPolicy.Select(RuleTarget.Phone, ConnectionId.Phone, current, AllAdapters(), new HashSet<ConnectionId> { current }, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physically_connected_lan_off_or_not_used_never_becomes_a_candidate_even_with_override(bool notUsed)
    {
        var adapters = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()],
            new AdapterOverrides(null, notUsed ? AdapterOverrides.NotUsed : null, "AA-BB-CC-00-00-18"), [ConnectionId.Lan]);
        foreach (var profile in Connections.All)
        foreach (var target in Enum.GetValues<RuleTarget>())
        {
            Assert.DoesNotContain(ConnectionId.Lan, SmartRoutingPolicy.Candidates(target, profile, ConnectionId.Lan, adapters, true));
            Assert.Null(SmartRoutingPolicy.Select(target, profile, ConnectionId.Lan, adapters, new HashSet<ConnectionId> { ConnectionId.Lan }, true));
        }
    }

    [Fact]
    public void Wifi_only_can_carry_home_and_disconnected_used_slots_wait_for_an_answer()
    {
        var onlyWifi = DetectionResult.Of(null, DetectionIssue.NotUsed, null, DetectionIssue.NotUsed, TestAdapters.Wifi(), DetectionIssue.None);
        Assert.Equal([ConnectionId.Wifi], SmartRoutingPolicy.Candidates(RuleTarget.Home, ConnectionId.Phone, ConnectionId.Wifi, onlyWifi, false));
        Assert.Equal(ConnectionId.Wifi, SmartRoutingPolicy.Select(RuleTarget.Home, ConnectionId.Phone, ConnectionId.Wifi, onlyWifi, new HashSet<ConnectionId> { ConnectionId.Wifi }, false));
        var disconnected = DetectionResult.Of(null, DetectionIssue.NotUsed, null, DetectionIssue.NotUsed, null, DetectionIssue.NotFound);
        Assert.Equal([ConnectionId.Wifi], SmartRoutingPolicy.Candidates(RuleTarget.Home, ConnectionId.Phone, ConnectionId.Wifi, disconnected, false));
        Assert.Null(SmartRoutingPolicy.Select(RuleTarget.Home, ConnectionId.Phone, ConnectionId.Wifi, disconnected, new HashSet<ConnectionId>(), false));
    }

    [Fact]
    public void Invalid_connection_mode_and_target_identities_throw()
    {
        var invalid = (ConnectionId)999;
        var badTarget = (RuleTarget)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.TargetOf(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.Tag(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.SelectorTag(badTarget));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.ProfileOf(NetworkStatus.Initial((RoutingMode)999, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.WantedExit(NetworkStatus.Initial((RoutingMode)999, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.Candidates(badTarget, ConnectionId.Phone, ConnectionId.Phone, AllAdapters(), false));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.Candidates(RuleTarget.Home, invalid, ConnectionId.Phone, AllAdapters(), false));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartRoutingPolicy.Candidates(RuleTarget.Home, ConnectionId.Phone, invalid, AllAdapters(), false));
    }
}
