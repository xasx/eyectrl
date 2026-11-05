namespace EyeCtrl.Setup.Display;

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Sdk.Abstractions;
using EyeCtrl.Sdk.Wpf;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Q8's gaze-operable uninstall choice: two screens in one borderless, topmost window. Screen 1
/// offers Keep (default), Cancel and Delete; Delete opens screen 2, where the safe answer sits
/// exactly under the button the gaze just dwelt on and the destructive answer is far to the left.
///
/// <para>
/// Every screen is armed only after <see cref="ArmingDelay"/>; a click or dwell before that is
/// ignored, so a gaze resting where the window appeared cannot fire anything. Close, Esc and
/// <see cref="ChoiceTimeout"/> all mean <see cref="UninstallChoice.Cancel"/> — nothing changes.
/// </para>
///
/// <para>
/// fixup-05: only the screen in front carries dwell targets, and losing the foreground (or being
/// hidden) removes them all. Regaining it re-registers fresh target ids — the App discards a
/// removed target with its accumulated dwell — so a window that was covered while a gaze rested
/// over it needs a full fresh dwell, never the 0.8 s remainder the sitting measured.
/// </para>
///
/// <para>
/// The window is never shown by a test: the fit tests lay its content out at 2.0 and the logic
/// tests raise the real routed events and drive the internal timers.
/// </para>
/// </summary>
internal class UninstallChoiceWindow : Window
{
    /// <summary>Every screen is armed after this delay.</summary>
    public static readonly TimeSpan ArmingDelay = TimeSpan.FromSeconds(1);

    /// <summary>No choice within this time means cancel.</summary>
    public static readonly TimeSpan ChoiceTimeout = TimeSpan.FromMinutes(5);

    private const string KeepAutomationId = "Setup_UninstallKeep";
    private const string CancelAutomationId = "Setup_UninstallCancel";
    private const string DeleteAutomationId = "Setup_UninstallDelete";
    private const string ConfirmDeleteAutomationId = "Setup_UninstallConfirmDelete";
    private const string KeepDataAutomationId = "Setup_UninstallKeepData";

    private readonly StringTable _strings;
    private readonly DispatcherTimer _armTimer;
    private readonly DispatcherTimer _choiceTimer;
    private readonly Grid _screen1;
    private readonly Grid _screen2;
    private bool _armed;
    private bool _showingConfirm;
    private bool _closingForResult;

    /// <summary>Builds the window from the resolved table. Nothing is shown until <see cref="ShowChoice"/>.</summary>
    /// <param name="strings">The resolved table.</param>
    /// <param name="workArea">The work area to lay the window into; tests pass the owner's
    /// 1920 x 1152 DIP, production passes <see cref="SystemParameters.WorkArea"/>.</param>
    public UninstallChoiceWindow(StringTable strings, Rect? workArea = null)
    {
        _strings = strings;

        Title = strings.T("uninstall.choice.title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Tokens.BrushSurfaceDark;
        Foreground = Tokens.BrushTextPrimary;
        FontFamily = Tokens.FontStandard;
        FontSize = SetupWindowMetrics.ProseFontSize;
        UseLayoutRounding = true;

        var bounds = SetupWindowMetrics.ChoiceBounds(workArea ?? SystemParameters.WorkArea);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        _screen1 = BuildScreen(confirm: false);
        _screen2 = BuildScreen(confirm: true);
        _screen2.Visibility = Visibility.Collapsed;

        var host = new Grid();
        host.Children.Add(_screen1);
        host.Children.Add(_screen2);
        Content = new Border
        {
            Background = Tokens.BrushSurfaceDark,
            Padding = new Thickness(SetupWindowMetrics.WindowMargin),
            Child = host,
        };

        _armTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ArmingDelay };
        _armTimer.Tick += (_, _) =>
        {
            _armed = true;
            _armTimer.Stop();
        };

        _choiceTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ChoiceTimeout };
        _choiceTimer.Tick += (_, _) => OnTimeout();

        Loaded += (_, _) =>
        {
            // fixup-05: only the screen in front is dwellable, and every (re)appearance starts a
            // fresh dwell — a gaze that rested here while another window covered this one has
            // accumulated nothing.
            ArmDwellTargets();
            _choiceTimer.Start();
        };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                ArmDwellTargets();
            else
                SuspendDwellTargets();
        };

        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// fixup-05 point 3: a covered or background window must not fire a long-armed dwell when it
    /// is uncovered. Losing the foreground drops every target (the App's detector discards its
    /// progress with the target); regaining it registers fresh ids, so a full dwell is needed
    /// again, plus the ordinary 1 s arming gate.
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ArmDwellTargets();
    }

    /// <inheritdoc />
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        SuspendDwellTargets();
    }

    /// <summary>The choice, or null while the window is still open.</summary>
    internal UninstallChoice? Result { get; private set; }

    /// <summary>What the last choice was; false until the current screen's arming delay elapsed.</summary>
    internal bool IsArmed => _armed;

    /// <summary>True while screen 2 (confirm delete) is showing.</summary>
    internal bool ShowingConfirm => _showingConfirm;

    /// <summary>True while the current screen's 1 s arming timer is running.</summary>
    internal bool IsArming => _armTimer.IsEnabled;

    /// <summary>True while the window's 5-minute no-choice timer is running.</summary>
    internal bool IsChoiceTimerRunning => _choiceTimer.IsEnabled;

    internal Button KeepButton { get; private set; } = null!;
    internal Button CancelButton { get; private set; } = null!;
    internal Button DeleteButton { get; private set; } = null!;
    internal Button ConfirmDeleteButton { get; private set; } = null!;
    internal Button KeepDataButton { get; private set; } = null!;

    /// <summary>Shows the window modally and returns the choice; an unasked window is a cancel.</summary>
    internal UninstallChoice ShowChoice()
    {
        ShowDialog();
        return Result ?? UninstallChoice.Cancel;
    }

    /// <summary>The result a window that was never answered resolves to.</summary>
    internal UninstallChoice ResolveUnaskedResult() => Result ?? UninstallChoice.Cancel;

    /// <summary>Completes the 1 s arming delay without waiting for a dispatcher timer.</summary>
    internal void CompleteArmingForTests()
    {
        _armTimer.Stop();
        _armed = true;
    }

    /// <summary>Runs the 5-minute timeout's own path.</summary>
    internal void ExpireForTests() => OnTimeout();

    /// <summary>
    /// The production button factory. Also used by the fit class, so the measured control is the
    /// button the window draws: 420 x 160 DIP, 28 DIP wrapping text, theme tokens, and the SDK's
    /// Invoke-mode dwell.
    /// </summary>
    internal static Button CreateChoiceButton(
        string label, ButtonKind kind, string automationId, Action action)
    {
        var (background, foreground, weight) = kind switch
        {
            ButtonKind.Primary => (Tokens.BrushAccent, Tokens.BrushSurfaceDark, Tokens.FontWeightStrong),
            ButtonKind.Danger => (Tokens.BrushAccentDanger, Tokens.BrushSurfaceDark, Tokens.FontWeightMedium),
            _ => (Tokens.BrushSurfaceMedium, Tokens.BrushTextPrimary, Tokens.FontWeightMedium),
        };

        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontFamily = Tokens.FontStandard,
            FontSize = SetupWindowMetrics.ChoiceFontSize,
            FontWeight = weight,
            Foreground = foreground,
        };

        var button = new Button
        {
            Content = text,
            Width = SetupWindowMetrics.ChoiceButtonWidth,
            Height = SetupWindowMetrics.ChoiceButtonHeight,
            Padding = new Thickness(16),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = background,
            Foreground = foreground,
            BorderBrush = Tokens.BrushBorderSubtle,
            BorderThickness = new Thickness(Tokens.Current.BorderThicknessStandard),
            FontFamily = Tokens.FontStandard,
            FontSize = SetupWindowMetrics.ChoiceFontSize,
            Cursor = Cursors.Hand,
            UseLayoutRounding = true,
        };

        button.Click += (_, _) => action();
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, label);

        // Invoke mode: EyeCtrl's own dwell completes the button through UIA, with its ring.
        // Enabled is left to the window (fixup-05): only the screen in front is registered, and
        // each activation re-registers it, so a covered-then-uncovered window needs a fresh dwell.
        Dwell.SetMode(button, DwellMode.Invoke);
        return button;
    }

    /// <summary>
    /// The production prose factory: one centred, wrapping 36 DIP line in its 1280 DIP box.
    /// The <c>setupProse</c> fit class measures exactly this control.
    /// </summary>
    internal static TextBlock CreateProse(string text, bool strong = false, bool secondary = false, bool dim = false)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = SetupWindowMetrics.ProseWidth,
            FontFamily = Tokens.FontStandard,
            FontSize = SetupWindowMetrics.ProseFontSize,
            FontWeight = strong ? Tokens.FontWeightStrong : Tokens.FontWeightMedium,
            Foreground = dim
                ? Tokens.BrushTextDim
                : secondary ? Tokens.BrushTextSecondary : Tokens.BrushTextPrimary,
            Margin = new Thickness(0, 0, 0, 12),
        };
    }

    private Grid BuildScreen(bool confirm)
    {
        var screen = new Grid();
        screen.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prose = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = SetupWindowMetrics.ProseWidth,
            Margin = new Thickness(0, 24, 0, 0),
        };

        if (confirm)
        {
            prose.Children.Add(CreateProse(_strings.T("uninstall.confirmDelete.title"), strong: true));
            prose.Children.Add(CreateProse(_strings.T("uninstall.confirmDelete.detail"), secondary: true));
        }
        else
        {
            prose.Children.Add(CreateProse(_strings.T("uninstall.choice.title"), strong: true));
            prose.Children.Add(CreateProse(_strings.T("uninstall.choice.question")));
            prose.Children.Add(CreateProse(_strings.T("uninstall.choice.detail"), secondary: true));
            prose.Children.Add(CreateProse(_strings.T("uninstall.choice.noChoice"), dim: true));
        }

        Grid.SetRow(prose, 0);
        screen.Children.Add(prose);

        var row = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = SetupWindowMetrics.ChoiceButtonHeight,
            Margin = new Thickness(0, 0, 0, SetupWindowMetrics.BottomMargin),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SetupWindowMetrics.ChoiceButtonWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SetupWindowMetrics.ChoiceGap) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SetupWindowMetrics.ChoiceButtonWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SetupWindowMetrics.ChoiceGap) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SetupWindowMetrics.ChoiceButtonWidth) });
        Grid.SetRow(row, 1);
        screen.Children.Add(row);

        if (confirm)
        {
            // Q8: the gaze rests where Delete was; the SAFE answer is there, the destructive one
            // is at the far left, so the dwell that opened this screen cannot land on "yes".
            KeepDataButton = CreateChoiceButton(
                _strings.T("uninstall.confirmDelete.no"), ButtonKind.Primary, KeepDataAutomationId,
                () => { if (_armed) Handle(UninstallChoiceAction.KeepData); });
            ConfirmDeleteButton = CreateChoiceButton(
                _strings.T("uninstall.confirmDelete.yes"), ButtonKind.Danger, ConfirmDeleteAutomationId,
                () => { if (_armed) Handle(UninstallChoiceAction.ConfirmDelete); });
            Grid.SetColumn(ConfirmDeleteButton, 0);
            Grid.SetColumn(KeepDataButton, 4);
            row.Children.Add(ConfirmDeleteButton);
            row.Children.Add(KeepDataButton);
        }
        else
        {
            KeepButton = CreateChoiceButton(
                _strings.T("uninstall.choice.keep"), ButtonKind.Primary, KeepAutomationId,
                () => { if (_armed) Handle(UninstallChoiceAction.Keep); });
            CancelButton = CreateChoiceButton(
                _strings.T("uninstall.choice.cancel"), ButtonKind.Normal, CancelAutomationId,
                () => { if (_armed) Handle(UninstallChoiceAction.Cancel); });
            DeleteButton = CreateChoiceButton(
                _strings.T("uninstall.choice.delete"), ButtonKind.Normal, DeleteAutomationId,
                () => { if (_armed) Handle(UninstallChoiceAction.Delete); });
            Grid.SetColumn(KeepButton, 0);
            Grid.SetColumn(CancelButton, 2);
            Grid.SetColumn(DeleteButton, 4);
            row.Children.Add(KeepButton);
            row.Children.Add(CancelButton);
            row.Children.Add(DeleteButton);
        }

        return screen;
    }

    private void Handle(UninstallChoiceAction action)
    {
        switch (action)
        {
            case UninstallChoiceAction.Keep:
                Complete(UninstallChoice.Keep);
                break;
            case UninstallChoiceAction.Cancel:
            case UninstallChoiceAction.KeepData:
                Complete(UninstallChoice.Cancel);
                break;
            case UninstallChoiceAction.Delete:
                ShowConfirm();
                break;
            case UninstallChoiceAction.ConfirmDelete:
                Complete(UninstallChoice.Delete);
                break;
        }
    }

    /// <summary>Every choice button, whatever screen it belongs to.</summary>
    private IEnumerable<Button> AllButtons =>
        [KeepButton, CancelButton, DeleteButton, ConfirmDeleteButton, KeepDataButton];

    private bool IsCurrentScreen(Button button) => _showingConfirm
        ? button == KeepDataButton || button == ConfirmDeleteButton
        : button == KeepButton || button == CancelButton || button == DeleteButton;

    /// <summary>
    /// Makes only the screen in front dwellable, re-registering its three targets. Disabling first
    /// is deliberate: the SDK opens a NEW target id on enable, and the App's detector drops the old
    /// target with its accumulated progress, so this call is the dwell restart. Called on Loaded,
    /// on every activation, and when the visible screen changes.
    /// </summary>
    private void ArmDwellTargets()
    {
        foreach (var button in AllButtons)
        {
            Dwell.SetEnabled(button, false);
            if (IsCurrentScreen(button))
                Dwell.SetEnabled(button, true);
        }

        StartArming();
    }

    /// <summary>
    /// Drops every dwell target while the window is covered or in the background. The App removes
    /// the target and its progress, so a gaze resting over a button cannot keep arming it; the
    /// 1 s gate is stopped too, so it cannot elapse unseen either.
    /// </summary>
    private void SuspendDwellTargets()
    {
        foreach (var button in AllButtons)
            Dwell.SetEnabled(button, false);

        _armed = false;
        _armTimer.Stop();
    }

    private void ShowConfirm()
    {
        _showingConfirm = true;
        _screen1.Visibility = Visibility.Collapsed;
        _screen2.Visibility = Visibility.Visible;
        Title = _strings.T("uninstall.confirmDelete.title");
        ArmDwellTargets();
    }

    private void StartArming()
    {
        _armed = false;
        _armTimer.Stop();
        _armTimer.Start();
    }

    private void OnTimeout()
    {
        _choiceTimer.Stop();
        if (Result is null)
            Complete(UninstallChoice.Cancel);
    }

    private void OnEscape()
    {
        _choiceTimer.Stop();
        if (Result is null)
            Complete(UninstallChoice.Cancel);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        OnEscape();
    }

    private void Complete(UninstallChoice choice)
    {
        if (Result is not null)
            return;

        Result = choice;
        _armTimer.Stop();
        _choiceTimer.Stop();
        _closingForResult = true;
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4, a programmatic close or any path that is not an answer means "nothing changes".
        if (!_closingForResult)
        {
            _armTimer.Stop();
            _choiceTimer.Stop();
            Result ??= UninstallChoice.Cancel;
        }

        base.OnClosing(e);
    }
}

/// <summary>The choice action a button carries.</summary>
internal enum UninstallChoiceAction
{
    Keep,
    Cancel,
    Delete,
    ConfirmDelete,
    KeepData,
}

/// <summary>The primary/default, danger and ordinary button faces.</summary>
internal enum ButtonKind
{
    Normal,
    Primary,
    Danger,
}

/// <summary>The production Q8 seam: shows the window and returns its choice. A window that cannot
/// be built at all is a cancel — the safe answer — never a delete.</summary>
public sealed class WpfUninstallChoice : IUninstallChoice
{
    private readonly SetupUi _ui;

    /// <summary>Creates the seam.</summary>
    public WpfUninstallChoice(SetupUi ui) => _ui = ui;

    /// <inheritdoc />
    public UninstallChoice Ask()
    {
        try
        {
            return new UninstallChoiceWindow(_ui.Strings).ShowChoice();
        }
        catch (Exception)
        {
            return UninstallChoice.Cancel;
        }
    }
}
