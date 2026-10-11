namespace EyeCtrl.Platform.Tests;

/// <summary>
/// Upper bounds for waits that are expected to SUCCEED: an event that must arrive, a pipe that must
/// connect. A passing run returns the moment the event fires, so a generous bound costs nothing; it
/// only stops a loaded CI runner (many test assemblies in parallel, such as the headless fit and
/// geometry gates) from failing a correct test. At 2 s, two such waits failed on CI in one evening.
/// Never use it for a wait that is expected to time out.
/// </summary>
internal static class TestTimeouts
{
    internal const int PositiveWaitMs = 10_000;
}
