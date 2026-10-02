namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

public class ActionMenuModifierButtonsTests
{
    [Fact]
    public void Definitions_TopToBottom_ShiftCtrlAlt_AtMirroredOffsets()
    {
        var defs = ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>());

        Check.That(defs).HasSize(3);
        Check.That(defs[0].Id).IsEqualTo(ActionMenuModifierButtons.ShiftId);
        Check.That(defs[0].VerticalOffset).IsStrictlyLessThan(0);
        Check.That(defs[1].Id).IsEqualTo(ActionMenuModifierButtons.CtrlId);
        Check.That(defs[1].VerticalOffset).IsEqualTo(0);
        Check.That(defs[2].Id).IsEqualTo(ActionMenuModifierButtons.AltId);
        Check.That(defs[2].VerticalOffset).IsEqualTo(-defs[0].VerticalOffset);
        Check.That(defs[0].Label).IsEqualTo("Shift");
        Check.That(defs.All(d => d.RearmOnGazeExit)).IsTrue();
    }

    [Fact]
    public void LatchedSet_SwapsToTheLatchedIconVariant()
    {
        var latched = new HashSet<ModifierKey> { ModifierKey.Ctrl };
        var defs = ActionMenuModifierButtons.Definitions(600, latched);

        Check.That(defs[0].Icon).IsEqualTo(ActionIcon.Shift);
        Check.That(defs[1].Icon).IsEqualTo(ActionIcon.CtrlLatched);
    }

    [Fact]
    public void WithLatched_SwapsOnlyTheIcon()
    {
        var def = ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>())[1];

        var on = ActionMenuModifierButtons.WithLatched(def, latched: true);
        var off = ActionMenuModifierButtons.WithLatched(on, latched: false);

        Check.That(on.Icon).IsEqualTo(ActionIcon.CtrlLatched);
        Check.That(on.DwellMs).IsEqualTo(def.DwellMs);
        Check.That(on.Activate).IsSameReferenceAs(def.Activate); // record `with`: same delegate instance
        Check.That(off.Icon).IsEqualTo(ActionIcon.Ctrl);
    }
}
