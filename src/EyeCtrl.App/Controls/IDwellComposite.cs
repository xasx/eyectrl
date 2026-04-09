namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows.Controls;

public interface IDwellComposite
{
    void Attach(Panel parent);
    void Detach(Panel parent);
    IEnumerable<DwellControl> Children { get; }
}
