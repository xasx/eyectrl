namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;

public class DmlDeviceChooserTests
{
    private static DmlDeviceInfo IntelIpc() => new(
        Description: "Intel(R) Graphics",
        Discrete: false,
        HighPerformanceIndex: 1,
        AdapterNumber: 0,
        VideoMemoryMb: 128);

    private static DmlDeviceInfo Nvidia() => new(
        Description: "NVIDIA GeForce RTX 5090 Laptop GPU",
        Discrete: true,
        HighPerformanceIndex: 0,
        AdapterNumber: 1,
        VideoMemoryMb: 24137);

    [Fact]
    public void Choose_HybridLaptop_PicksTheDiscreteGpuByItsHighPerformanceRank()
    {
        var chosen = DmlDeviceChooser.Choose([IntelIpc(), Nvidia()]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.Description).IsEqualTo("NVIDIA GeForce RTX 5090 Laptop GPU");
    }

    [Fact]
    public void Choose_SingleAdapter_ReturnsIt()
    {
        var chosen = DmlDeviceChooser.Choose([IntelIpc()]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.Description).IsEqualTo("Intel(R) Graphics");
    }

    [Fact]
    public void Choose_NoAdapters_ReturnsNull()
    {
        Check.That(DmlDeviceChooser.Choose([])).IsNull();
    }

    [Fact]
    public void Choose_WithoutHighPerformanceIndex_PrefersTheDiscreteAdapter()
    {
        var integrated = new DmlDeviceInfo("Intel Graphics", Discrete: false, HighPerformanceIndex: null,
            AdapterNumber: 0, VideoMemoryMb: 32_000);
        var discrete = new DmlDeviceInfo("Some Discrete GPU", Discrete: true, HighPerformanceIndex: null,
            AdapterNumber: 1, VideoMemoryMb: 8_000);

        var chosen = DmlDeviceChooser.Choose([integrated, discrete]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.Description).IsEqualTo("Some Discrete GPU");
    }

    [Fact]
    public void Choose_WithoutAnyRankingOrDiscreteness_PrefersTheLargerVideoMemory()
    {
        var small = new DmlDeviceInfo("Small", Discrete: null, HighPerformanceIndex: null,
            AdapterNumber: 0, VideoMemoryMb: 4_000);
        var large = new DmlDeviceInfo("Large", Discrete: null, HighPerformanceIndex: null,
            AdapterNumber: 1, VideoMemoryMb: 24_000);

        var chosen = DmlDeviceChooser.Choose([small, large]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.Description).IsEqualTo("Large");
    }

    [Fact]
    public void Choose_TwoDiscreteAdapters_PicksTheLowestHighPerformanceIndex()
    {
        var second = new DmlDeviceInfo("Second", Discrete: true, HighPerformanceIndex: 2,
            AdapterNumber: 0, VideoMemoryMb: 16_000);
        var first = new DmlDeviceInfo("First", Discrete: true, HighPerformanceIndex: 0,
            AdapterNumber: 1, VideoMemoryMb: 8_000);

        var chosen = DmlDeviceChooser.Choose([second, first]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.Description).IsEqualTo("First");
    }

    [Fact]
    public void Choose_IdenticalMetadata_PicksTheLowerAdapterNumber()
    {
        var higher = new DmlDeviceInfo("Twin", Discrete: true, HighPerformanceIndex: 0,
            AdapterNumber: 5, VideoMemoryMb: 8_000);
        var lower = new DmlDeviceInfo("Twin", Discrete: true, HighPerformanceIndex: 0,
            AdapterNumber: 2, VideoMemoryMb: 8_000);

        var chosen = DmlDeviceChooser.Choose([higher, lower]);

        Check.That(chosen).IsNotNull();
        Check.That(chosen!.AdapterNumber).IsEqualTo(2);
    }

    [Fact]
    public void Parse_RealAdapterMetadata_ReadsEveryField()
    {
        var metadata = new Dictionary<string, string>
        {
            ["Description"] = "NVIDIA GeForce RTX 5090 Laptop GPU",
            ["LUID"] = "86836",
            ["DxgiAdapterNumber"] = "1",
            ["DxgiHighPerformanceIndex"] = "0",
            ["DxgiVideoMemory"] = "24137 MB",
            ["Discrete"] = "1",
        };

        var info = DmlDeviceChooser.Parse(metadata);

        Check.That(info.Description).IsEqualTo("NVIDIA GeForce RTX 5090 Laptop GPU");
        Check.That(info.Discrete).IsEqualTo(true);
        Check.That(info.HighPerformanceIndex).IsEqualTo(0);
        Check.That(info.AdapterNumber).IsEqualTo(1);
        Check.That(info.VideoMemoryMb).IsEqualTo(24137L);
    }

    [Fact]
    public void Parse_IntegratedMetadata_ReadsDiscreteZeroAsFalse()
    {
        var metadata = new Dictionary<string, string>
        {
            ["Description"] = "Intel(R) Graphics",
            ["DxgiAdapterNumber"] = "0",
            ["DxgiHighPerformanceIndex"] = "1",
            ["DxgiVideoMemory"] = "128 MB",
            ["Discrete"] = "0",
        };

        var info = DmlDeviceChooser.Parse(metadata);

        Check.That(info.Discrete).IsEqualTo(false);
        Check.That(info.HighPerformanceIndex).IsEqualTo(1);
        Check.That(info.VideoMemoryMb).IsEqualTo(128L);
    }

    [Fact]
    public void Parse_EmptyMetadata_ReturnsAllNulls()
    {
        var info = DmlDeviceChooser.Parse(new Dictionary<string, string>());

        Check.That(info.Description).IsNull();
        Check.That(info.Discrete).IsNull();
        Check.That(info.HighPerformanceIndex).IsNull();
        Check.That(info.AdapterNumber).IsNull();
        Check.That(info.VideoMemoryMb).IsNull();
    }

    [Fact]
    public void Parse_GarbageValues_BecomeNullAndNeverThrow()
    {
        var metadata = new Dictionary<string, string>
        {
            ["Description"] = "   ",
            ["DxgiAdapterNumber"] = "one",
            ["DxgiHighPerformanceIndex"] = "",
            ["DxgiVideoMemory"] = "about 24 GB",
            ["Discrete"] = "maybe",
        };

        var info = DmlDeviceChooser.Parse(metadata);

        Check.That(info.Description).IsNull();
        Check.That(info.Discrete).IsNull();
        Check.That(info.HighPerformanceIndex).IsNull();
        Check.That(info.AdapterNumber).IsNull();
        Check.That(info.VideoMemoryMb).IsNull();
    }

    [Fact]
    public void Parse_BareVideoMemoryNumber_IsAcceptedAsMegabytes()
    {
        var info = DmlDeviceChooser.Parse(new Dictionary<string, string> { ["DxgiVideoMemory"] = "8192" });

        Check.That(info.VideoMemoryMb).IsEqualTo(8192L);
    }

    [Fact]
    public void Parse_KeyLookup_IsCaseInsensitive()
    {
        var info = DmlDeviceChooser.Parse(new Dictionary<string, string>
        {
            ["description"] = "Case Tolerant GPU",
            ["discrete"] = "TRUE",
        });

        Check.That(info.Description).IsEqualTo("Case Tolerant GPU");
        Check.That(info.Discrete).IsEqualTo(true);
    }
}
