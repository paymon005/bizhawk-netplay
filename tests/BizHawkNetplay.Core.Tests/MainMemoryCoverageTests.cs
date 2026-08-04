using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Whether hashing MainMemory covers the machine at all.
///
/// The real shapes below are taken from BizHawk 2.11.1's own domain registration: GBHawkLink4x
/// registers Main RAM A/B/C/D and nominates none, so MemoryDomainList.MainMemory falls back to
/// the first — and a desync confined to P2/P3/P4's machine would never be seen.
/// </summary>
public class MainMemoryCoverageTests
{
    [Fact]
    public void GBHawkLink4xIsCaught()
    {
        // Verbatim from GBHawkLink4x.SetupMemoryDomains.
        string[] domains =
        [
            "Main RAM A", "Main RAM B", "Main RAM C", "Main RAM D",
            "Zero Page RAM A", "Zero Page RAM B", "Zero Page RAM C", "Zero Page RAM D",
            "System Bus A", "System Bus B", "System Bus C", "System Bus D",
            "ROM A", "ROM B", "ROM C", "ROM D",
            "VRAM A", "VRAM B", "VRAM C", "VRAM D",
            "Cart RAM L", "Cart RAM B", "Cart RAM C", "Cart RAM D",
        ];

        Assert.True(MainMemoryCoverage.IsSingleMachineSlice("Main RAM A", domains));
        Assert.Equal(new[] { "Main RAM B", "Main RAM C", "Main RAM D" },
            MainMemoryCoverage.SiblingMachines("Main RAM A", domains));
    }

    [Fact]
    public void TheTwoAndThreeMachineLinkCoresAreCaughtToo()
    {
        string[] link2 = ["Main RAM L", "Main RAM R", "System Bus L", "System Bus R"];
        Assert.True(MainMemoryCoverage.IsSingleMachineSlice("Main RAM L", link2));
        Assert.Equal(new[] { "Main RAM R" }, MainMemoryCoverage.SiblingMachines("Main RAM L", link2));

        string[] link3 = ["Main RAM L", "Main RAM C", "Main RAM R"];
        Assert.True(MainMemoryCoverage.IsSingleMachineSlice("Main RAM L", link3));
        Assert.Equal(2, MainMemoryCoverage.SiblingMachines("Main RAM L", link3).Count);
    }

    [Theory]
    // Every ordinary core: one machine, nothing missed. These are the real MainMemory names.
    [InlineData("RDRAM")]          // N64
    [InlineData("WRAM")]           // SNES
    [InlineData("RAM")]            // NES
    [InlineData("Main RAM")]       // Genesis / single GBHawk
    [InlineData("68K RAM")]
    [InlineData("MainRAM")]
    public void OrdinaryCoresAreNotRefused(string mainMemory)
    {
        string[] domains = [mainMemory, "ROM", "System Bus", "VRAM", "Save RAM"];
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice(mainMemory, domains));
        Assert.Empty(MainMemoryCoverage.SiblingMachines(mainMemory, domains));
    }

    [Fact]
    public void ASuffixWithNoSiblingIsNotAMultiMachineCore()
    {
        // A lone letter-suffixed domain means nothing is being missed — the test is the SIBLING,
        // not the suffix, or any core with a "Bank A" would be refused for no reason.
        string[] domains = ["Main RAM A", "ROM", "System Bus"];
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("Main RAM A", domains));
    }

    [Fact]
    public void ASiblingUnderADifferentPrefixDoesNotCount()
    {
        // "Cart RAM B" is not a sibling of "Main RAM A": different prefix, different thing.
        string[] domains = ["Main RAM A", "Cart RAM B", "ROM"];
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("Main RAM A", domains));
    }

    [Fact]
    public void DegenerateInputIsSafe()
    {
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice(null, ["Main RAM A", "Main RAM B"]));
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("", ["Main RAM A"]));
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("Main RAM A", null));
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("A", ["A", "B"]));       // no prefix
        Assert.False(MainMemoryCoverage.IsSingleMachineSlice("Main RAM 1", ["Main RAM 2"])); // digit, not a machine letter
        Assert.Empty(MainMemoryCoverage.SiblingMachines(null, null));
    }

    [Fact]
    public void SiblingsComeBackInAFixedOrderWhateverTheDomainListSays()
    {
        // The checksum folds these in sequence, so the order is part of the hash. Two peers whose
        // IMemoryDomains happened to enumerate differently would compute different values for
        // byte-identical states and report a desync that does not exist — and it would be permanent,
        // because nothing about a resync would change the enumeration order.
        string[] forwards = ["Main RAM A", "Main RAM B", "Main RAM C", "Main RAM D", "ROM"];
        string[] backwards = ["ROM", "Main RAM D", "Main RAM C", "Main RAM B", "Main RAM A"];

        var fromForwards = MainMemoryCoverage.SiblingMachines("Main RAM A", forwards);
        var fromBackwards = MainMemoryCoverage.SiblingMachines("Main RAM A", backwards);

        Assert.Equal(new[] { "Main RAM B", "Main RAM C", "Main RAM D" }, fromForwards);
        Assert.Equal(fromForwards, fromBackwards);
    }

    [Fact]
    public void TheSiblingListNeverIncludesTheDomainAlreadyBeingHashed()
    {
        // Folding the primary in twice would be harmless for detection but would make the value
        // depend on which machine MainMemory resolved to, which is not something both peers are
        // guaranteed to agree on for the same reason the coverage bug existed in the first place.
        string[] domains = ["Main RAM A", "Main RAM B", "Main RAM C"];
        foreach (var primary in domains)
            Assert.DoesNotContain(primary, MainMemoryCoverage.SiblingMachines(primary, domains));
    }
}
