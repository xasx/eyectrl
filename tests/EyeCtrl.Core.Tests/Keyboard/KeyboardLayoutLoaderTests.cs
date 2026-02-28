namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;
using System.Text.Json;

public class KeyboardLayoutLoaderTests
{
    [Fact]
    public void Load_ValidJson_ReturnsLayout()
    {
        var json = """
        {
            "name": "QWERTY",
            "rows": [
                [
                    { "label": "Q", "output": "q", "shiftOutput": "Q", "width": 1.0 },
                    { "label": "W", "output": "w", "shiftOutput": "W", "width": 1.0 }
                ],
                [
                    { "label": "A", "output": "a", "shiftOutput": "A", "width": 1.0 }
                ]
            ]
        }
        """;

        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, json);
            var layout = KeyboardLayoutLoader.Load(tempPath);

            Assert.Equal("QWERTY", layout.Name);
            Assert.Equal(2, layout.Rows.Count);
            Assert.Equal(2, layout.Rows[0].Count);
            Assert.Equal("Q", layout.Rows[0][0].Label);
            Assert.Equal("q", layout.Rows[0][0].Output);
            Assert.Equal("Q", layout.Rows[0][0].ShiftOutput);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
