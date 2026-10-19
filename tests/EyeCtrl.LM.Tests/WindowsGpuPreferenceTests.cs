namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;

public class WindowsGpuPreferenceTests
{
    [Fact]
    public void ParseGpuPreference_PowerSaving_ReturnsOne()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("GpuPreference=1;")).IsEqualTo(1);
    }

    [Fact]
    public void ParseGpuPreference_HighPerformanceAmongOtherPairs_ReturnsTwo()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("SwapEffectUpgradeEnable=1;GpuPreference=2;"))
            .IsEqualTo(2);
    }

    [Fact]
    public void ParseGpuPreference_WithoutTrailingSemicolon_ReturnsTheValue()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("GpuPreference=1")).IsEqualTo(1);
    }

    [Fact]
    public void ParseGpuPreference_KeyLookup_IsCaseInsensitive()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("gpupreference=2;")).IsEqualTo(2);
    }

    [Fact]
    public void ParseGpuPreference_MissingValue_ReturnsNull()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("SwapEffectUpgradeEnable=1;")).IsNull();
    }

    [Fact]
    public void ParseGpuPreference_GarbageValue_ReturnsNull()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference("GpuPreference=banana;")).IsNull();
    }

    [Fact]
    public void ParseGpuPreference_NullOrEmpty_ReturnsNull()
    {
        Check.That(WindowsGpuPreference.ParseGpuPreference(null)).IsNull();
        Check.That(WindowsGpuPreference.ParseGpuPreference("")).IsNull();
        Check.That(WindowsGpuPreference.ParseGpuPreference("   ")).IsNull();
    }

    [Fact]
    public void ForCurrentProcess_ReadsWithoutThrowing()
    {
        // Read-only: the test never writes the key, whatever this machine has stored.
        Check.ThatCode(WindowsGpuPreference.ForCurrentProcess).DoesNotThrow();
    }
}
