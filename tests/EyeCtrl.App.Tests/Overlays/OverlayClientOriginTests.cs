namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// T13 fixup-05/07: every overlay window's CLIENT origin must equal its WINDOW origin. Several
/// overlays compute a target in physical screen coordinates and draw it back through the window
/// origin (<c>OverlayPositioning.CoverPrimaryScreen</c> sets <c>Left/Top</c> = the window origin,
/// <c>DrawHideButton</c> converts the physical centre to logical client coordinates), so a window
/// that carries a non-client frame — a resize border — draws its content that many DIP right and
/// below the place its target says.
///
/// <para>
/// The glass backdrop did exactly that: the default <c>ResizeMode="CanResize"</c> on a
/// non-transparent window gave it a 12 px (6 DIP at 2.0) resize border, so its ✕, dark fill and hint
/// were drawn 6 DIP right and below their places (found by the T13 live check). The fix is
/// <c>ResizeMode="NoResize"</c>, as <c>EdgeButtonBarWindow</c> already had.
/// </para>
///
/// <para>
/// <b>Every subclass, no silent skips.</b> The set is discovered by reflection over
/// <see cref="OverlayWindow"/> — every concrete subclass, not only the parameterless-constructor
/// ones — and pinned below. All-optional constructors are invoked through
/// <see cref="BindingFlags.OptionalParamBinding"/> with <see cref="Type.Missing"/>;
/// <see cref="NoteWindow"/> and <see cref="SettingsWindow"/> get explicit factories (the latter on a
/// temp settings file with a GUID writer mutex, the T12 live-check harness's construction, never
/// shown). A subclass whose simplest constructor needs non-optional arguments, or that fails to
/// build, is a FAILURE here — never a skip — so a new overlay shape forces a factory rather than
/// quietly escaping the gate.
/// </para>
///
/// <para>
/// <b>Handle only, no <c>Show()</c>.</b> The styles that produce the frame exist as soon as the HWND
/// is created (<see cref="WindowInteropHelper.EnsureHandle"/>), so this runs in the headless suite
/// and paints nothing on the owner's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class OverlayClientOriginTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point32 point);

    /// <summary>
    /// Every concrete <see cref="OverlayWindow"/> subclass, pinned so a new one cannot silently
    /// escape this gate. <see cref="OverlayWindow"/> itself is excluded: it is the base class (a bare
    /// window with the default caption), never shown as an overlay.
    /// </summary>
    private static readonly string[] ExpectedOverlays =
    [
        "ActionMenuWindow",
        "ActivationButtonWindow",
        "CalibrationWindow",
        "CaregiverAlertWindow",
        "CueOverlayWindow",
        "CursorCircleWindow",
        "DragTrailOverlay",
        "DriftNoticeWindow",
        "DwellFeedbackOverlay",
        "EdgeButtonBarWindow",
        "EyeCareBannerWindow",
        "EyeCareExerciseWindow",
        "GlassBackdropWindow",
        "KeyboardWindow",
        "ManualSelectionGazeIndicatorWindow",
        "ManualSelectionOverlayWindow",
        "ModesMenuWindow",
        "NoteWindow",
        "PauseIndicatorWindow",
        "QuickActionsGridWindow",
        "RepeatCursorWindow",
        "RestartNoticeWindow",
        "ScrollOverlayWindow",
        "SettingsWindow",
        "SplashWindow",
        "SubPixelProbeWindow",
        "TargetHighlightWindow",
        "TaskSwitcherWindow",
        "ToolbarWindow",
        "TrackStatusWindow",
    ];

    [StaFact]
    public void EveryOverlayWindow_StartsItsClientAtItsWindowOrigin()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var types = OverlayTypes();

        Check.WithCustomMessage(
                "The set of concrete OverlayWindow subclasses changed. Check the new window here (its client origin "
                + "must equal its window origin) and add it to ExpectedOverlays so this gate keeps covering every "
                + "overlay.")
            .That(types.Select(t => t.Name).ToList()).IsEqualTo(ExpectedOverlays.ToList());

        var cleanups = new List<Action>();
        var problems = new List<string>();
        try
        {
            foreach (var type in types)
            {
                Window? window = null;
                try
                {
                    window = Build(type, cleanups);
                    var hwnd = new WindowInteropHelper(window).EnsureHandle();
                    var inset = ClientOriginMinusWindowOrigin(hwnd);
                    if (inset.X != 0 || inset.Y != 0)
                    {
                        problems.Add(
                            $"{type.Name}: the client origin is ({inset.X},{inset.Y}) px inside the window "
                            + $"(ResizeMode={window.ResizeMode}, AllowsTransparency={window.AllowsTransparency}); "
                            + "anything drawn in client coordinates lands that far off its target.");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(
                        $"{type.Name}: could not be built or measured ({ex.GetType().Name}: {ex.Message}); add a "
                        + "factory to this gate so the overlay is really covered.");
                }
                finally
                {
                    try { window?.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
                }
            }
        }
        finally
        {
            foreach (var cleanup in cleanups)
            {
                try { cleanup(); } catch { /* best effort */ }
            }
        }

        Check.WithCustomMessage(
                "An overlay window has a non-client frame, so its client origin does not equal its window origin "
                + "(set ResizeMode=\"NoResize\" on it, as EdgeButtonBarWindow and GlassBackdropWindow do), or it "
                + "could not be built:\n  " + string.Join("\n  ", problems))
            .That(problems).IsEmpty();
    }

    /// <summary>
    /// Every concrete <see cref="OverlayWindow"/> subclass in this assembly, by name — no constructor
    /// filter: a class that cannot be built here is reported as a failure by the caller.
    /// </summary>
    private static List<Type> OverlayTypes() =>
        typeof(OverlayWindow).Assembly.GetTypes()
            .Where(t => typeof(OverlayWindow).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && t != typeof(OverlayWindow)
                        && t.Assembly == typeof(OverlayWindow).Assembly)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Builds one overlay window without showing it. The two classes whose constructors this gate
    /// cannot fill generically get explicit factories; anything else must have a public constructor
    /// whose parameters are all optional — invoked through <see cref="BindingFlags.OptionalParamBinding"/>
    /// with <see cref="Type.Missing"/> so the defaults apply — or it fails loudly.
    /// </summary>
    private static Window Build(Type type, List<Action> cleanups)
    {
        if (type == typeof(NoteWindow)) return new NoteWindow("probe");
        if (type == typeof(SettingsWindow)) return BuildSettingsWindow(cleanups);
        if (type == typeof(SubPixelProbeWindow)) return BuildSubPixelProbeWindow();

        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("it has no public constructor");
        var parameters = ctor.GetParameters();
        if (parameters.Any(parameter => !parameter.IsOptional))
        {
            throw new InvalidOperationException(
                $"its shortest public constructor ({parameters.Length} parameter(s)) has non-optional arguments");
        }

        return (Window)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.OptionalParamBinding,
            binder: null,
            args: parameters.Length == 0 ? null : parameters.Select(_ => Type.Missing).ToArray(),
            culture: null)!;
    }

    /// <summary>
    /// The probe's headless construction (the SP-4d contrast harness's shape): a real view model
    /// over a seeded schedule and default dwell settings, no backdrop, scale 2.0, never shown.
    /// </summary>
    private static SubPixelProbeWindow BuildSubPixelProbeWindow() =>
        new(null,
            new SubPixelProbeViewModel(
                new WeakReferenceMessenger(),
                new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
                SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
                new EyeCtrlSettings().Dwell,
                () => true),
            new CursorSettings(),
            () => { },
            viewFault: () => { },
            scale: 2.0);

    /// <summary>
    /// The T12 live-check harness's construction of the real <see cref="SettingsWindow"/>, without
    /// <c>Show()</c>: a temp settings file under a GUID-named writer mutex, a temp model scope, and
    /// no-op restart/supervision services. Nothing can reach the owner's <c>%APPDATA%</c>.
    /// </summary>
    internal static SettingsWindow BuildSettingsWindow(List<Action> cleanups, IMessenger? sharedMessenger = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "eyectrl_t13_client_origin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var models = ModelTestFakes.Create();
        cleanups.Add(() => { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } });
        cleanups.Add(models.Dispose);

        var settings = new EyeCtrlSettings();
        var messenger = sharedMessenger ?? new WeakReferenceMessenger();
        var service = new SettingsService(
            Path.Combine(root, "settings.json"), SettingsWriter.App,
            @"Local\EyeCtrl.Settings.Writer.test_" + Guid.NewGuid().ToString("N"));
        var editor = new SettingsQuickActionsEditorViewModel(
            new QaTestSupport.FakeQuickActionsStore(), new QaTestSupport.FakeInstalledAppProvider());
        editor.Load();
        var viewModel = new SettingsWindowViewModel(
            new SettingsViewModel(settings), editor, messenger, settings.Dwell);
        var (registry, management) = models.WithDownloadedBundled();
        return new SettingsWindow(
            viewModel, settings, service, registry, management, messenger,
            new CalibrationService(), Restart(settings), Supervision(settings));
    }

    private static AppRestartCoordinator Restart(EyeCtrlSettings settings) =>
        new(messenger: new WeakReferenceMessenger(),
            settings: settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => { },
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => true,
            bringUp: new SupervisorBringUp(
                settings, () => { }, () => false, _ => { }, () => { }, () => false));

    private static SupervisionToggleService Supervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(
                settings, () => { }, () => false, _ => { }, () => { }, () => false),
            new SupervisorStartupEnsure(
                settings, () => { }, () => false, () => false),
            persistEnabled: _ => { },
            supervisorInstalled: () => false,
            isSupervisorConnected: () => false,
            reconcileRunKey: () => { },
            runInBackground: work => work(),
            postToUi: work => work());

    /// <summary>The client area's top-left in screen pixels minus the window rect's top-left, both physical px.</summary>
    private static Point32 ClientOriginMinusWindowOrigin(IntPtr hwnd)
    {
        var origin = new Point32 { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        GetWindowRect(hwnd, out var rect);
        return new Point32 { X = origin.X - rect.Left, Y = origin.Y - rect.Top };
    }
}
