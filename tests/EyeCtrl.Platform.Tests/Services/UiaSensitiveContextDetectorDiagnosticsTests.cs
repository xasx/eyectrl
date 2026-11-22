namespace EyeCtrl.Platform.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// The 21.11 password-detector diagnostics (D1-D3, fixup-01 F2/F3b/F3c/F4): verdict transitions, the first
/// focus event per process, the first read failure per process at Warning, the session's counter line and the
/// live counter snapshot. The decision/logging seam takes primitives, so these run headless — no real UI
/// Automation read and no live element. Levels are asserted explicitly: the owner's day file starts at
/// Information, so a line moved to Debug is a no-show even while its text still matches.
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

    /// <summary>A fixed control type for the headless seam; the production read is a deferred UIA read.</summary>
    private static Func<string> ControlType(string name) => () => name;

    [Fact]
    public void AVerdictChange_IsLoggedOnceAtInformation_WithTheProcessSourceAndControlType()
    {
        var (detector, log) = New();

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));   // no change
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: false, readControlType: ControlType("Document"));

        Check.That(log.Snapshot().All(e => e.Level == LogLevel.Information)).IsTrue();
        Check.That(log.Messages()).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=true controlType=Edit",
            "Sensitive context: on proc=msedge.exe source=event controlType=Edit",
            "Sensitive context: off proc=msedge.exe source=event controlType=Document");
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void TheFirstEventOfEachProcess_IsLoggedExactlyOnceAtInformation_PerSession()
    {
        var (detector, log) = New();

        detector.ObserveFocusEvent(1, App, isPassword: false, readControlType: ControlType("Edit"));
        detector.ObserveFocusEvent(1, "MSEDGE.EXE", isPassword: true, readControlType: ControlType("Edit"));   // same process
        detector.ObserveFocusEvent(2, "notepad.exe", isPassword: false, readControlType: ControlType("Document"));

        var firsts = log.Snapshot()
            .Where(e => e.Message.StartsWith("UIA focus event: first", StringComparison.Ordinal))
            .ToArray();
        Check.That(firsts.All(e => e.Level == LogLevel.Information)).IsTrue();
        Check.That(firsts.Select(e => e.Message)).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=false controlType=Edit",
            "UIA focus event: first from proc=notepad.exe isPassword=false controlType=Document");
    }

    [Fact]
    public void OwnProcessFocus_IsLoggedAsNoread_AndRetainsTheVerdict()
    {
        var (detector, log) = New();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));

        detector.ObserveFocusEvent(Environment.ProcessId, "EyeCtrl.exe", isPassword: null, readControlType: null);

        // The keyboard/overlay focus must not clear an active password context (the user types the password
        // with it), and no IsPassword read is attempted (nor claimed in the line).
        Check.That(detector.IsSensitiveActive).IsTrue();
        Check.That(log.Messages()).Contains(
            "UIA focus event: first from proc=EyeCtrl.exe isPassword=noread controlType=-");
        Check.That(log.Messages().Any(m => m.StartsWith("Sensitive context: off", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void Stop_LogsTheOffTransitionAndTheEventCount_ExactlyOnceAtInformation()
    {
        var (detector, log) = New();
        detector.Start();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));

        detector.Stop();
        detector.Stop();   // idempotent: no second off, no second count line

        Check.That(log.Snapshot().All(e => e.Level == LogLevel.Information)).IsTrue();
        Check.That(log.Messages()).ContainsExactly(
            "UIA focus event: first from proc=msedge.exe isPassword=true controlType=Edit",
            "Sensitive context: on proc=msedge.exe source=event controlType=Edit",
            "Sensitive context: off proc=msedge.exe source=stop controlType=-",
            "UIA focus events: 1 (password 1, read failures 0, own 0)");
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void TheFirstReadFailureOfEachProcess_IsAWarning_LaterOnesAreDebug_AndItsFirstEventSaysError()
    {
        var (detector, log) = New();
        detector.Start();
        var first = new InvalidOperationException(Marker);
        var second = new COMException(Marker);

        // msedge: two failing reads. The first failure of the PROCESS is the Warning, the repeat is Debug, and
        // that process's first-event line says error (so "every Edge read throws" is visible live).
        detector.ObserveReadFailure(App, first);
        detector.ObserveReadFailure(App, second);
        // A different process has its own first failure at Warning: the session's first is not the only one.
        detector.ObserveReadFailure("notepad.exe", second);
        detector.Stop();

        var entries = log.Snapshot();
        Check.That(entries[0].Level).IsEqualTo(LogLevel.Information);
        Check.That(entries[0].Message).IsEqualTo(
            "UIA focus event: first from proc=msedge.exe isPassword=error controlType=-");
        Check.That(entries[1].Level).IsEqualTo(LogLevel.Warning);
        Check.That(entries[1].Message).IsEqualTo(
            $"UIA focus read failed: proc=msedge.exe type=InvalidOperationException hresult=0x{first.HResult:X8}");
        Check.That(entries[2].Level).IsEqualTo(LogLevel.Debug);
        Check.That(entries[2].Message).IsEqualTo(
            $"UIA focus read failed again: proc=msedge.exe type=COMException hresult=0x{second.HResult:X8}");
        Check.That(entries[3].Level).IsEqualTo(LogLevel.Information);
        Check.That(entries[3].Message).IsEqualTo(
            "UIA focus event: first from proc=notepad.exe isPassword=error controlType=-");
        Check.That(entries[4].Level).IsEqualTo(LogLevel.Warning);
        Check.That(entries[4].Message).IsEqualTo(
            $"UIA focus read failed: proc=notepad.exe type=COMException hresult=0x{second.HResult:X8}");
        Check.That(entries[5].Message).IsEqualTo("UIA focus events: 3 (password 0, read failures 3, own 0)");

        // The provider's message could carry content, so no line may contain it — or the exception object.
        Check.That(entries.Any(e => e.Message.Contains(Marker, StringComparison.Ordinal))).IsFalse();
        Check.That(entries.All(e => e.Exception is null)).IsTrue();
        Check.That(detector.IsSensitiveActive).IsFalse();
    }

    [Fact]
    public void AProcessWhoseFirstReadFailed_DoesNotLogASecondFirstEventLine_WhenItLaterReads()
    {
        var (detector, log) = New();
        detector.ObserveReadFailure(App, new InvalidOperationException("fault"));

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));

        Check.That(log.Messages().Count(m => m.StartsWith("UIA focus event: first", StringComparison.Ordinal)))
            .IsEqualTo(1);
        Check.That(log.Messages()).Contains("Sensitive context: on proc=msedge.exe source=event controlType=Edit");
    }

    [Fact]
    public void TheSessionCount_SplitsPasswordFailuresAndOwnProcessEvents_AndTheLiveSnapshotMatches()
    {
        var (detector, log) = New();
        detector.Start();

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: false, readControlType: ControlType("Edit"));
        detector.ObserveFocusEvent(Environment.ProcessId, "EyeCtrl.exe", isPassword: null, readControlType: null);
        detector.ObserveReadFailure(App, new InvalidOperationException("read fault"));

        // F3a: the same counters are readable live, before Stop.
        Check.That(detector.GetFocusEventCounts()).IsEqualTo(new FocusEventCounts(4, 1, 1, 1));

        detector.Stop();

        var counter = log.Snapshot().Single(e => e.Message.StartsWith("UIA focus events:", StringComparison.Ordinal));
        Check.WithCustomMessage("The counter line must stay at Information: the day file starts there.")
            .That(counter.Level).IsEqualTo(LogLevel.Information);
        Check.That(counter.Message).IsEqualTo("UIA focus events: 4 (password 1, read failures 1, own 1)");
    }

    [Fact]
    public void ANoElementFocus_IsCounted_ClearsTheVerdict_AndCannotLeakTheSendersText()
    {
        var (detector, log) = New();
        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType: ControlType("Edit"));

        // sender is not an AutomationElement: the best-effort not-sensitive path, which must log event facts
        // only. A mutant that logs the sender (or its ToString) would put the marker on a line.
        detector.OnFocusChanged(new MarkerCarrier(), new AutomationFocusChangedEventArgs(0, 0));

        Check.That(detector.IsSensitiveActive).IsFalse();
        Check.That(log.Messages().Any(m => m.Contains(Marker, StringComparison.Ordinal))).IsFalse();
        Check.That(log.Messages()).Contains("Sensitive context: off proc=- source=event controlType=-");
        Check.That(log.Messages()).Contains("UIA focus event: first from proc=unknown isPassword=noread controlType=-");
    }

    [Fact]
    public void TheControlType_IsReadOnlyForFirstEventsAndTransitions_AndOnlyAfterTheVerdictIsPublished()
    {
        // F4: the control type is a second cross-process UIA read. It must not run on the hot path: only a
        // process's first event and a verdict change carry it, and the verdict must already be published when
        // the read happens (so a slow provider cannot delay the gate).
        var (detector, _) = New();
        int reads = 0;
        bool? verdictWhenRead = null;
        Func<string> readControlType = () =>
        {
            reads++;
            verdictWhenRead = detector.IsSensitiveActive;
            return "Edit";
        };

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType);
        Check.That(reads).IsEqualTo(1);                  // first event AND transition, one read
        Check.That(verdictWhenRead == true).IsTrue();    // the verdict was published before the read
        Check.That(detector.IsSensitiveActive).IsTrue();

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: true, readControlType);   // repeat, no change
        Check.That(reads).IsEqualTo(1);                  // no diagnostic read on the hot path

        detector.ObserveFocusEvent(ForeignPid, App, isPassword: false, readControlType);  // transition
        Check.That(reads).IsEqualTo(2);
        Check.That(verdictWhenRead == false).IsTrue();
        Check.That(detector.IsSensitiveActive).IsFalse();
    }
}
