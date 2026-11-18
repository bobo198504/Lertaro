using Lertaro.Plugins.CoreExtensions.Providers.InstantAnswers;

namespace Lertaro.Plugins.CoreExtensions.Tests.Providers.InstantAnswers;

[TestClass]
public sealed class HostsFileInstantProviderTests
{
    [TestMethod]
    [DataRow("host")]
    [DataRow("HOST")]
    [DataRow("hosts")]
    [DataRow("HoStS")]
    public void GetInstantResults_MatchesTriggerCaseInsensitively(string query)
    {
        var provider = new HostsFileInstantProvider();

        Assert.HasCount(1, provider.GetInstantResults(query).ToList());
    }

    [TestMethod]
    public void GetInstantResults_DoesNotClaimLongerWords()
    {
        var provider = new HostsFileInstantProvider();

        Assert.IsEmpty(provider.GetInstantResults("hostname").ToList());
    }
}
