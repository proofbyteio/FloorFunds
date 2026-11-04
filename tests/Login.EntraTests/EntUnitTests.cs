using Login.Entra;
using Xunit;
namespace Login.EntraTests;

public class EntUnitTests
{
    [Theory, InlineData(8080), InlineData(8081), InlineData(18080), InlineData(18081), InlineData(18082), InlineData(5000)]
    public void ENT_PORT_01_guard_refuses_forbidden_ports(int p) => Assert.Throws<InvalidOperationException>(() => PortGuard.Check(p));

    [Fact] public void ENT_AMR_01_output_is_subset_of_closed_enum()
    {
        string[] all = ["mfa", "ngcmfa", "pwd", "otp", "wia", "rsa", "wiaormfa", "fed", "none", "x", "sms", "fido", "hwk"];
        Assert.All(AmrMapper.Map(all), a => Assert.Contains(a, AmrMapper.ClosedEnum));
        Assert.DoesNotContain("rsa", AmrMapper.Map(["rsa"]));
        Assert.Empty(AmrMapper.Map(null));
    }

    [Fact] public void ENT_HO_03_future_auth_time_beyond_skew_refused()
        => Assert.False(HandoffRule.Allows(["mfa"], DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));

    [Fact] public void ENT_SELF_01_login_entra_has_no_standin_dependency()
    {
        var refs = typeof(AmrMapper).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(refs, n => n!.Contains("StandIn", StringComparison.OrdinalIgnoreCase));
    }
}
