namespace EyeCtrl.Platform.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// The 21.11 password-detector diagnostics (D1-D3): verdict transitions, the first focus event per process,
/// the first read failure at Warning, and the session's one counter line. The decision/logging seam takes
/// primitives (<see cref="UiaSensitiveContextDetector.ObserveFocusEvent"/> and friends), so these run
/// headless — no real UI Automation read and no live element. The privacy contract is asserted too: a
/// sender whose text contains a marker must not put that marker on any line.
/// </summary>
public class UiaSensitiveContextDetectorDiagnosticsTests
{
    private const int ForeignPid = 4242;
    private const string App = "msedge.exe";
    private const string Marker = "Zebrafink-Marker";

    private sealed class Capture : ILogger<UiaSensitiveContextDetector>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception), exception));
        }

        public (LogLevel Level, string Message, Exception? Exception)[] Snapshot()
        {
            lock (_gate) return _entries.ToArray();
        }

        public string[] Messages() => Snapshot().Select(e => e.Message).ToArray();
    }

    private sealed class IdleHook : IFocusHookApi
    {
        public void Add(AutomationFocusChangedEventHandler handler) { }

        public void Remove(AutomationFocusChangedEventHandler handler) { }
    }

    /// <summary>A sender that is not an <see cref="AutomationElement"/> and carries text a line must never copy.</summary>
    private sealed class MarkerCarrier
    {
        public override string ToString() => Marker;
    }

    private static (UiaSensitiveContextDetector Detector, Capture Log) New()
    {
        var log = new Capture();
        return (new UiaSensitiveContextDetector(log, new IdleHook()), log);
    }

    [Fact]
    public void AVerdictChange_IsLoggedOnce_WithTheProcessAndSource_AndOnlyWhenItChanges()
    {
        var (detector, log) = New();

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");   // no change
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: false, controlTypeName: "Edit");

        Check.That(log.Messages()).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=true controlType=Edit",
            "Sensitive context: on proc=msedge.exe source=event",
            "Sensitive context: off proc=msedge.exe source=event");
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void TheFirstEventOfEachProcess_IsLoggedExactlyOnce_PerSession()
    {
        var (detector, log) = New();

        detector.ObserveFocusEvent(1, App, isPassword: false, controlTypeName: "Edit");
        detector.ObserveFocusEvent(1, "MSEDGE.EXE", isPassword: true, controlTypeName: "Edit");   // same process
        detector.ObserveFocusEvent(2, "notepad.exe", isPassword: false, controlTypeName: "Document");

        var firsts = log.Messages().Where(m => m.StartsWith("UIA focus event: first", StringComparison.Ordinal)).ToArray();
        Check.That(firsts).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=false controlType=Edit",
            "UIA focus event: first from proc=notepad.exe isPassword=false controlType=Document");
    }

    [Fact]
    public void OwnProcessFocus_IsLoggedAsNoread_AndRetainsTheVerdict()
    {
        var (detector, log) = New();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");

        detector.ObserveFocusEvent(Environment.ProcessId, "EyeCtrl.exe", isPassword: null, controlTypeName: null);

        // The keyboard/overlay focus must not clear an active password context (the user types the password
        // with it), and no IsPassword read is attempted (nor claimed in the line).
        Check.That(detector.IsSensitiveActive).IsTrue();
        Check.That(log.Messages()).Contains(
            "UIA focus event: first from proc=EyeCtrl.exe isPassword=noread controlType=-");
        Check.That(log.Messages().Any(m => m.StartsWith("Sensitive context: off", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void Stop_LogsTheOffTransition_AndTheEventCount_ExactlyOnce()
    {
        var (detector, log) = New();
        detector.Start();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");

        detector.Stop();
        detector.Stop();   // idempotent: no second off, no second count line

        Check.That(log.Messages()).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=true controlType=Edit",
            "Sensitive context: on proc=msedge.exe source=event",
            "Sensitive context: off proc=msedge.exe source=stop",
            "UIA focus events: 1 (password 1, read failures 0, own 0)");
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void TheFirstReadFailure_IsAWarning_WithTypeAndHResultOnly_LaterOnesAreDebug()
    {
        var (detector, log) = New();
        var first = new InvalidOperationException(Marker);
        var second = new COMException(Marker);

        detector.ObserveReadFailure(first);
        detector.ObserveReadFailure(second);

        var entries = log.Snapshot();
        Check.That(entries[0].Level).IsEqualTo(LogLevel.Warning);
        Check.That(entries[0].Message).IsEqualTo(
            $"UIA focus read failed: type=InvalidOperationException hresult=0x{first.HResult:X8}");
        Check.That(entries[1].Level).IsEqualTo(LogLevel.Debug);
        Check.That(entries[1].Message).IsEqualTo(
            $"UIA focus read failed again: type=COMException hresult=0x{second.HResult:X8}");

        // The provider's message could carry content, so neither line may contain it — or the exception object.
        Check.That(entries.Any(e => e.Message.Contains(Marker, StringComparison.Ordinal))).IsFalse();
        Check.That(entries.All(e => e.Exception is null)).IsTrue();
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void TheSessionCount_SplitsPasswordFailuresAndOwnProcessEvents()
    {
        var (detector, log) = New();
        detector.Start();

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: false, controlTypeName: "Edit");
        detector.ObserveFocusEvent(Environment.ProcessId, "EyeCtrl.exe", isPassword: null, controlTypeName: null);
        detector.ObserveReadFailure(new InvalidOperationException("read fault"));

        detector.Stop();

        Check.That(log.Messages()).Contains("UIA focus events: 4 (password 1, read failures 1, own 1)");
    }

    [Fact]
    public void ANoElementFocus_IsCounted_ClearsTheVerdict_AndCannotLeakTheSendersText()
    {
        var (detector, log) = New();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, controlTypeName: "Edit");

        // sender is not an AutomationElement: the best-effort not-sensitive path, which must log event facts
        // only. A mutant that logs the sender (or its ToString) would put the marker on a line.
        detector.OnFocusChanged(new MarkerCarrier(), new AutomationFocusChangedEventArgs(0, 0));

        Check.That(detector.IsSensitiveActive).IsFalse();
        Check.That(log.Messages().Any(m => m.Contains(Marker, StringComparison.Ordinal))).IsFalse();
        Check.That(log.Messages()).Contains("Sensitive context: off proc=- source=event");
        Check.That(log.Messages()).Contains("UIA focus event: first from proc=unknown isPassword=noread controlType=-");
    }
}
