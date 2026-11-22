namespace EyeCtrl.Core.Tests.Diagnostics;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Diagnostics;

/// <summary>
/// W1/W3 of crash-dialog-off-per-process: the helper really sets WER's process flag, and the
/// failed-HRESULT path returns one WRN line instead of throwing or pretending success.
/// <para>
/// Nothing here crashes, FailFasts or starts a crashing child. The real-API test changes only the
/// test host's own WER flags (its own crash UI), which is harmless and never reaches the screen.
/// </para>
/// </summary>
public class CrashDialogPolicyTests
{
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>WER_FAULT_REPORTING_NO_UI from werapi.h, spelled out as a literal so a mutant of
    /// the production constant cannot make this assertion agree with itself.</summary>
    private const uint NoUi = 0x20;

    [Fact]
    public void Disable_SetsTheNoUiFlag_OnThisProcess()
    {
        Check.That(CrashDialogPolicy.WerFaultReportingNoUi).IsEqualTo(NoUi);

        var outcome = CrashDialogPolicy.Disable();

        Check.That(outcome.Succeeded).IsTrue();
        Check.That(outcome.Line).IsEqualTo(CrashDialogPolicy.OffLine);

        uint hresult = WerGetFlags(GetCurrentProcess(), out uint flags);
        Check.That(hresult).IsEqualTo(0u);
        Check.WithCustomMessage(
                "WER_FAULT_REPORTING_NO_UI (0x20) is not set on the test host after Disable: "
                + "the crash dialog would still appear in every EyeCtrl process.")
            .That((flags & NoUi) != 0).IsTrue();
    }

    [Fact]
    public void Disable_ReturnsTheFailedHResult_AndDoesNotThrow()
    {
        var outcome = CrashDialogPolicy.Disable(_ => unchecked((uint)AccessDenied));

        Check.That(outcome.Succeeded).IsFalse();
        Check.That(outcome.HResult).IsEqualTo(AccessDenied);
        Check.That(outcome.Line).Contains("HRESULT 0x80070005");
    }

    [Fact]
    public void Failure_Report_WritesExactlyOneWarning_AndNoInfo()
    {
        var info = new List<string>();
        var warnings = new List<string>();

        CrashDialogPolicy.Disable(_ => unchecked((uint)AccessDenied)).Report(info.Add, warnings.Add);

        Check.That(info).IsEmpty();
        Check.That(warnings).HasSize(1);
        Check.WithCustomMessage("the WRN must name the HRESULT so the log explains the missing flag.")
            .That(warnings[0].Contains("HRESULT 0x80070005", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void Success_Report_WritesExactlyOneInfo_AndNoWarning()
    {
        var info = new List<string>();
        var warnings = new List<string>();

        CrashDialogPolicy.Disable(_ => 0).Report(info.Add, warnings.Add);

        Check.That(warnings).IsEmpty();
        Check.That(info).ContainsExactly(CrashDialogPolicy.OffLine);
    }

    [Fact]
    public void Disable_DoesNotThrow_WhenTheNativeCallThrows()
    {
        CrashDialogOutcome? outcome = null;

        var threw = false;
        try
        {
            outcome = CrashDialogPolicy.Disable(
                _ => throw new DllNotFoundException("kernel32 / WerSetFlags"));
        }
        catch
        {
            threw = true;
        }

        Check.WithCustomMessage("Disable must never throw into a process that is about to crash.")
            .That(threw).IsFalse();
        Check.That(outcome).IsNotNull();
        Check.That(outcome!.Succeeded).IsFalse();
        Check.That(outcome.HResult).IsNotEqualTo(0);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WerGetFlags(IntPtr hProcess, out uint pdwFlags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
}
