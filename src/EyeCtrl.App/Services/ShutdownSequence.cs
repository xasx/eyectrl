namespace EyeCtrl.App.Services;

using Serilog;

/// <summary>One named step of the exit sequence: a human name plus the work it does.</summary>
/// <param name="Name">The name the watchdog reports if this step hangs; also the per-step log line.</param>
/// <param name="Run">The step's work. Its exceptions are caught and logged, as before.</param>
internal sealed record ExitStep(string Name, Action Run);

/// <summary>
/// Runs <c>App.OnExit</c>'s steps in order and publishes the step it is inside, so
/// <see cref="ExitWatchdog"/> can name the one that never returned. Extracted from the local
/// <c>Step(string, Action)</c> closure in <c>OnExit</c> so the order and the blocking behaviour are
/// testable headlessly.
/// <para>
/// Every step is best-effort: an exception in one must not skip the steps after it — including the
/// single-instance mutex release and the log flush. The order itself is unchanged (the mutex is still
/// released last), except that the watchdog starts first and the splash dispose is now a named step.
/// </para>
/// </summary>
internal sealed class ShutdownSequence
{
    /// <summary>The name published before the first step runs.</summary>
    internal const string NotStarted = "(not started)";

    /// <summary>
    /// The name published after the last step: the process is in native teardown (dispatcher
    /// shutdown, <c>Main</c> return, DLL detach). A hang here is still reported, which is the reason
    /// the watchdog must not be cancelled when the sequence ends.
    /// </summary>
    internal const string NativeExitStep = "native exit after the last OnExit step";

    private readonly List<ExitStep> _steps = new();
    private readonly Action<string> _logStep;
    private readonly Action<string, Exception> _logFailure;
    private volatile string _currentStep = NotStarted;

    /// <summary>
    /// Creates an empty sequence. The two log actions exist so the tests can run the real sequence
    /// without writing into whichever Serilog sink another test in this process may have installed;
    /// production leaves both at their defaults.
    /// </summary>
    /// <param name="logStep">Receives each step name before it runs (defaults to Serilog Information).</param>
    /// <param name="logFailure">Receives a failed step and its exception (defaults to Serilog Error).</param>
    internal ShutdownSequence(
        Action<string>? logStep = null,
        Action<string, Exception>? logFailure = null)
    {
        _logStep = logStep ?? (name => Log.Information("OnExit step: {Step}", name));
        _logFailure = logFailure ?? ((name, ex) => Log.Error(ex, "OnExit step failed: {Step}", name));
    }

    /// <summary>The step currently running (or the last one finished), read by the watchdog thread.</summary>
    internal string CurrentStep => _currentStep;

    /// <summary>The step names, in run order.</summary>
    internal IReadOnlyList<string> StepNames => _steps.ConvertAll(step => step.Name);

    /// <summary>
    /// The steps themselves, in run order. Exists so a test can run one production step in isolation —
    /// the watchdog step is the one that must both start and see this sequence's own current step.
    /// </summary>
    internal IReadOnlyList<ExitStep> Steps => _steps;

    /// <summary>Appends a step. Call before <see cref="Run"/>.</summary>
    /// <param name="name">The step's name (see <see cref="ExitStep.Name"/>).</param>
    /// <param name="run">The step's work.</param>
    /// <returns>This sequence, for chaining.</returns>
    internal ShutdownSequence Add(string name, Action run)
    {
        _steps.Add(new ExitStep(name, run));
        return this;
    }

    /// <summary>
    /// Runs every step in order, logging <c>OnExit step: {Step}</c> at Information before each one
    /// and publishing its name first, so a hang is attributable. Exceptions are logged per step and
    /// never stop the sequence.
    /// </summary>
    internal void Run()
    {
        foreach (ExitStep step in _steps)
        {
            _currentStep = step.Name;
            _logStep(step.Name);
            try
            {
                step.Run();
            }
            catch (Exception ex)
            {
                _logFailure(step.Name, ex);
            }
        }

        _currentStep = NativeExitStep;
    }
}
