namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06-B: the raw <c>supervision.enabled</c> read. <c>SettingsService.Load()</c> answers a
/// damaged or unparseable file with whole-file defaults (enabled=false) without throwing, so the
/// supervisor must not ask Load() — that would silently disable recovery exactly when something is
/// already wrong. A null answer means "cannot tell"; the caller then acts on its last known value.
/// </summary>
public class SupervisionEnabledReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "eyectrl-supenabled-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void MissingFile_IsDisabled() =>
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsEqualTo(false);

    [Theory]
    [InlineData("""{"supervision":{"enabled":true}}""", true)]
    [InlineData("""{"supervision":{"enabled":false}}""", false)]
    [InlineData("""{"supervision":{}}""", false)]              // key missing -> default
    [InlineData("""{"other":1}""", false)]                     // section missing -> default
    [InlineData("""{"supervision":{"enabled":true},"other":1}""", true)]
    public void ValidFile_ReadsTheValue(string json, bool expected)
    {
        File.WriteAllText(_path, json);
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData("{ not json")]                                  // unparseable
    [InlineData("[1,2]")]                                       // non-object root
    [InlineData("""{"supervision":5}""")]                        // section is not an object
    [InlineData("""{"supervision":null}""")]                     // explicit null section is damaged, not "false"
    [InlineData("""{"supervision":{"enabled":"yes"}}""")]        // enabled is not a bool
    [InlineData("""{"supervision":{"enabled":null}}""")]         // explicit null value is damaged
    [InlineData("""{"supervision":{"enabled":true,"enabled":false}}""")] // duplicate key: ambiguous
    public void DamagedOrUnparseableFile_CannotTell(string json)
    {
        File.WriteAllText(_path, json);
        Check.That(SupervisionEnabledReader.TryRead(_path)).IsNull();
    }
}
