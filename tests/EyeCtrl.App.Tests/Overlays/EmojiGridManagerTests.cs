namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public class EmojiGridManagerTests
{
    /// <summary>
    /// What the manager handed the registrar for one dwell target. The registrar is the real
    /// <c>KeyboardWindow.RegisterKeyVisual</c> in production, and the content is what its outline
    /// wraps: passing <see langword="null"/> leaves the glyph out of the tree entirely, while
    /// parenting the content on the Border first makes the outline's Attach throw.
    /// </summary>
    private sealed record Registration(string Id, KeyDefinition Key, Border Container, TextBlock? Content);

    private static (EmojiGridManager Manager, List<Registration> Registrations) NewManager(
        IMessenger? messenger = null)
    {
        var registrations = new List<Registration>();
        var manager = new EmojiGridManager(
            keyRows: new StackPanel(),
            categoryBar: new StackPanel(),
            registerKeyVisual: (id, key, container, label, content) =>
                registrations.Add(new Registration(id, key, container, label)),
            getKeySize: () => 70,
            clearKeyState: () => { },
            messenger: messenger);
        return (manager, registrations);
    }

    private static string EmojiJsonPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");

    private static string TempRecentPath() =>
        Path.Combine(Path.GetTempPath(), "eyectrl_emoji_grid_test_" + Guid.NewGuid().ToString("N") + ".json");

    [StaFact]
    public void SetSkinToneAction_RaisesSkinToneChanged()
    {
        var (manager, _) = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:3");
        Check.That(raised).IsEqualTo(3);
        Check.That(manager.SkinTone).IsEqualTo(3);
    }

    [StaFact]
    public void SetSkinToneAction_ClampsOutOfRange()
    {
        var (manager, _) = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:9");
        Check.That(raised).IsEqualTo(5);
    }

    [StaFact]
    public void SetSkinTone_ProgrammaticInitialization_DoesNotRaiseEvent()
    {
        var (manager, _) = NewManager();
        var raised = false;
        manager.SkinToneChanged += _ => raised = true;
        manager.SetSkinTone(2);
        Check.That(raised).IsFalse();
    }

    /// <summary>
    /// Every grid surface must hand its content to the registrar unwrapped. With the outline
    /// attached inside <c>RegisterKeyVisual</c>, the 4th argument IS the cell's glyph: <c>null</c>
    /// installs an empty outline and the emoji vanishes from a grid that still accumulates dwell,
    /// and leaving the Border's own Child set makes Attach throw on an already-parented element.
    /// </summary>
    [StaFact]
    public void Enter_HandsEachGridSurfaceItsContent_WithoutParentingIt()
    {
        var (manager, registrations) = NewManager();
        manager.SetEmojiData(EmojiJsonPath(), TempRecentPath());

        manager.Enter();

        Check.WithCustomMessage(
                "The emoji grid registered no dwell targets at all — the shipped emoji data did not "
                + "load, so every assertion below would pass vacuously.")
            .That(registrations).Not.IsEmpty();

        var cells = registrations.Where(r => r.Id.StartsWith("emoji_")).ToList();
        Check.WithCustomMessage("No emoji cell was registered.").That(cells).Not.IsEmpty();
        foreach (var cell in cells)
        {
            Check.WithCustomMessage($"{cell.Id} handed the registrar no content, so the outline "
                    + "would wrap nothing and the glyph would silently vanish.")
                .That(cell.Content).IsInstanceOf<Emoji.Wpf.TextBlock>();
            Check.WithCustomMessage($"{cell.Id} handed over its glyph but also parented it on the "
                    + "cell Border, so the registrar's outline Attach would throw.")
                .That(cell.Container.Child).IsNull();
        }

        var specials = registrations.Where(r => r.Id.StartsWith("special_")).ToList();
        Check.WithCustomMessage("No special button (ABC / skin tone / paging) was registered.")
            .That(specials).Not.IsEmpty();
        foreach (var special in specials)
        {
            Check.WithCustomMessage($"{special.Id} handed the registrar no content, so its label "
                    + "would silently vanish under the outline.")
                .That(special.Content).IsNotNull();
            Check.That(special.Container.Child).IsNull();
        }
    }

    [StaFact]
    public void ShowSkinToneSelector_HandsEachSwatchItsContent_WithoutParentingIt()
    {
        var (manager, registrations) = NewManager();
        manager.SetEmojiData(EmojiJsonPath(), TempRecentPath());
        manager.Enter();
        registrations.Clear();

        manager.HandleAction("skinTone");

        var swatches = registrations.Where(r => r.Id.StartsWith("skin_")).ToList();
        Check.WithCustomMessage(
                "Expected the six skin-tone swatches; the selector did not build or the emoji data "
                + "did not load.")
            .That(swatches).HasSize(6);
        foreach (var swatch in swatches)
        {
            Check.WithCustomMessage($"{swatch.Id} handed the registrar no content, so the preview "
                    + "emoji would silently vanish under the outline.")
                .That(swatch.Content).IsInstanceOf<Emoji.Wpf.TextBlock>();
            Check.That(swatch.Container.Child).IsNull();
        }
    }

    /// <summary>
    /// Counts <see cref="DwellActivationMessage"/> arrivals on an isolated messenger — the
    /// dwell-completion cue's trigger. A record with a mutable counter so the facts can assert
    /// "exactly once" rather than merely "subscribed". <paramref name="effectRanAtSend"/> is
    /// captured at RECEIVE time: it proves the handler's effect already ran when the cue fires
    /// (spec D8, external-review mutation teeth — a publish-before-effect reorder otherwise
    /// keeps Count == 1 green).
    /// </summary>
    private sealed record CueProbe(Action OnReceive, Func<bool>? EffectRanAtSend = null)
        : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public bool? EffectAtSend { get; private set; }

        public void Receive(DwellActivationMessage message)
        {
            Count++;
            EffectAtSend = EffectRanAtSend?.Invoke();
            OnReceive();
        }
    }

    /// <summary>
    /// The category detector has no public "add a target without showing UI" path:
    /// <c>UpdateCategoryTargetPositions</c> early-returns until the real tab bar has laid out,
    /// which never happens headless. S4/S5: reflect the field, add the target directly, and
    /// feed the detector itself — no window, no layout, no launch.
    /// </summary>
    private static DwellDetector CategoryDetectorOf(EmojiGridManager manager) =>
        (DwellDetector)typeof(EmojiGridManager)
            .GetField("_categoryDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

    private static void DriveCategoryDwell(EmojiGridManager manager, string targetId)
    {
        var detector = CategoryDetectorOf(manager);
        detector.AddTarget(new DwellTarget(targetId, new PhysicalPoint(50, 50), 30, 25));
        for (var i = 1; i <= 20; i++)
            detector.Update(new PhysicalPoint(50, 50), i * 2_000);
    }

    private static T FieldOf<T>(EmojiGridManager manager, string name) =>
        (T)typeof(EmojiGridManager)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    [StaFact]
    public void CategorySwitch_PublishesDwellActivation_AfterTheEffect()
    {
        var messenger = new WeakReferenceMessenger();
        var (manager, _) = NewManager(messenger);
        var before = FieldOf<string>(manager, "_activeEmojiCategory");
        var probe = new CueProbe(() => { },
            EffectRanAtSend: () => FieldOf<string>(manager, "_activeEmojiCategory") != before);
        messenger.Register(probe);

        DriveCategoryDwell(manager, "cat_smileys"); // category-switch branch

        Check.That(probe.Count).IsEqualTo(1);
        Check.WithCustomMessage(
            "The category switch must land BEFORE the cue publishes (spec D8 — a publish-first " +
            "reorder leaves Count == 1 green and must not).")
            .That(probe.EffectAtSend).IsEqualTo(true);
    }

    [StaFact]
    public void SkinToneSelector_PublishesDwellActivation_AfterTheEffect()
    {
        var messenger = new WeakReferenceMessenger();
        var (manager, _) = NewManager(messenger);
        // ShowSkinToneSelector rebuilds the grid into _keyRows (its swatch cells land there,
        // per CountLiveCells), so a changed child count is the branch's effect observable.
        var before = FieldOf<System.Windows.Controls.StackPanel>(manager, "_keyRows").Children.Count;
        var probe = new CueProbe(() => { },
            EffectRanAtSend: () =>
                FieldOf<System.Windows.Controls.StackPanel>(manager, "_keyRows").Children.Count != before);
        messenger.Register(probe);

        DriveCategoryDwell(manager, "cat__skinTone"); // early-return branch (spec review 2 #3)

        Check.That(probe.Count).IsEqualTo(1);
        Check.WithCustomMessage(
            "The skin-tone selector must open BEFORE the cue publishes (spec D8) — this early-" +
            "return branch is exactly the one an end-of-handler publish would leave silent.")
            .That(probe.EffectAtSend).IsEqualTo(true);
    }

    [StaFact]
    public void NoMessengerGiven_LandsOnTheRegisteredDefaultBus()
    {
        // D9 (external-review S3): KeyboardWindow constructs the manager with no messenger, so
        // the fallback IS the production path — pin that it is the app's registered instance
        // (AppBootstrapper registers WeakReferenceMessenger.Default as IMessenger), not a
        // private bus the cue service would never hear.
        var (manager, _) = NewManager(null);
        var bus = FieldOf<object>(manager, "_messenger");
        Check.That(bus).IsSameReferenceAs(WeakReferenceMessenger.Default);
    }
}
