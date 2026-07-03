namespace EyeCtrl.App.Tests.Overlays;

using System.Windows.Controls;
using EyeCtrl.App.Overlays;

public class EmojiGridManagerTests
{
    private static EmojiGridManager NewManager() => new(
        keyRows: new StackPanel(),
        categoryBar: new StackPanel(),
        registerKeyVisual: (_, _, _, _) => { },
        getKeySize: () => 70,
        clearKeyState: () => { });

    [StaFact]
    public void SetSkinToneAction_RaisesSkinToneChanged()
    {
        var manager = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:3");
        Check.That(raised).IsEqualTo(3);
        Check.That(manager.SkinTone).IsEqualTo(3);
    }

    [StaFact]
    public void SetSkinToneAction_ClampsOutOfRange()
    {
        var manager = NewManager();
        int? raised = null;
        manager.SkinToneChanged += tone => raised = tone;
        manager.HandleAction("setSkinTone:9");
        Check.That(raised).IsEqualTo(5);
    }

    [StaFact]
    public void SetSkinTone_ProgrammaticInitialization_DoesNotRaiseEvent()
    {
        var manager = NewManager();
        var raised = false;
        manager.SkinToneChanged += _ => raised = true;
        manager.SetSkinTone(2);
        Check.That(raised).IsFalse();
    }
}
