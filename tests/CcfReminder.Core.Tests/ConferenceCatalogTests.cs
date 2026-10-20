using CcfReminder.Core;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>六个会议的固定身份与显示顺序（不含任何截止日期）。</summary>
public class ConferenceCatalogTests
{
    [Fact]
    public void SixIdentities_InFixedDisplayOrder()
    {
        Assert.Equal(6, ConferenceCatalog.All.Count);
        Assert.Equal(
            new[] { "ICML", "ICLR", "NeurIPS", "AAMAS", "AAAI", "DAI" },
            ConferenceCatalog.All.Select(c => c.ShortName).ToArray());
        Assert.Equal(
            new[] { "icml", "iclr", "nips", "aamas", "aaai", "dai" },
            ConferenceCatalog.All.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void NeurIPS_UpstreamIdIsNips_DisplayNameIsNeurIPS()
    {
        var neurips = ConferenceCatalog.All.Single(c => c.ShortName == "NeurIPS");
        Assert.Equal("nips", neurips.Id);
        Assert.DoesNotContain(ConferenceCatalog.All, c => c.ShortName == "NIPS");
    }

    [Fact]
    public void Dai_IsDistributedArtificialIntelligence()
    {
        var dai = ConferenceCatalog.All.Single(c => c.Id == "dai");
        Assert.Equal("International Conference on Distributed Artificial Intelligence", dai.FullName);
    }
}
