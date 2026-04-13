namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;
using System.Text.Json;

public class KeyboardLayoutLoaderTests
{
    [Fact]
    public void Load_ValidJson_ReturnsLayoutWithSubLayouts()
    {
        var json = """
        {
            "name": "QWERTY",
            "defaultSubLayout": "letters",
            "subLayouts": {
                "letters": {
                    "label": "ABC",
                    "rows": [
                        [
                            { "label": "Q", "output": "q", "shiftOutput": "Q", "width": 1.0 },
                            { "label": "W", "output": "w", "shiftOutput": "W", "width": 1.0 }
                        ],
                        [
                            { "label": "A", "output": "a", "shiftOutput": "A", "width": 1.0 }
                        ],
                        [
                            { "label": "123", "output": "", "width": 1.5, "specialAction": "switchSub:numbers" }
                        ]
                    ]
                },
                "numbers": {
                    "label": "123",
                    "rows": [
                        [
                            { "label": "1", "output": "1", "width": 1.0 },
                            { "label": "2", "output": "2", "width": 1.0 }
                        ],
                        [
                            { "label": "ABC", "output": "", "width": 1.5, "specialAction": "switchSub:letters" }
                        ]
                    ]
                }
            }
        }
        """;

        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, json);
            var layout = KeyboardLayoutLoader.Load(tempPath);

            Assert.Equal("QWERTY", layout.Name);
            Assert.Equal("letters", layout.DefaultSubLayout);
            Assert.Equal(2, layout.SubLayouts.Count);
            Assert.True(layout.SubLayouts.ContainsKey("letters"));
            Assert.True(layout.SubLayouts.ContainsKey("numbers"));

            var letters = layout.SubLayouts["letters"];
            Assert.Equal("ABC", letters.Label);
            Assert.Equal(3, letters.Rows.Count);
            Assert.Equal("Q", letters.Rows[0][0].Label);
            Assert.Equal("q", letters.Rows[0][0].Output);
            Assert.Equal("Q", letters.Rows[0][0].ShiftOutput);

            // Switch button
            var switchKey = letters.Rows[2][0];
            Assert.Equal("switchSub:numbers", switchKey.SpecialAction);

            var numbers = layout.SubLayouts["numbers"];
            Assert.Equal("123", numbers.Label);
            Assert.Equal(2, numbers.Rows.Count);
            Assert.Equal("1", numbers.Rows[0][0].Label);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_QwertyJson_VerifiesModifierKeysAreCorrectlyParsed()
    {
        // Locate the qwerty.json file relative to the test assembly location
        // AppContext.BaseDirectory points to bin/Debug/net10.0
        var testBinDir = AppContext.BaseDirectory;
        var layoutPath = Path.Combine(testBinDir, "..", "..", "..", "..", "..", "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json");
        var absolutePath = Path.GetFullPath(layoutPath);

        Assert.True(File.Exists(absolutePath), $"qwerty.json not found at {absolutePath}");

        var layout = KeyboardLayoutLoader.Load(absolutePath);

        Assert.Equal("QWERTY", layout.Name);
        Assert.True(layout.SubLayouts.ContainsKey("letters"), "Layout should contain 'letters' sub-layout");

        var letters = layout.SubLayouts["letters"];
        Assert.Equal(4, letters.Rows.Count);

        // Row 0, first key: Win modifier
        var row0Key0 = letters.Rows[0][0];
        Assert.Equal("modifier:win", row0Key0.SpecialAction);

        // Row 1, first key: Caps Lock toggle
        var row1Key0 = letters.Rows[1][0];
        Assert.Equal("toggle:capslock", row1Key0.SpecialAction);

        // Row 2, first key: Alt modifier
        var row2Key0 = letters.Rows[2][0];
        Assert.Equal("modifier:alt", row2Key0.SpecialAction);

        // Row 2, second key: Shift modifier
        var row2Key1 = letters.Rows[2][1];
        Assert.Equal("modifier:shift", row2Key1.SpecialAction);

        // Row 3, first key: Ctrl modifier
        var row3Key0 = letters.Rows[3][0];
        Assert.Equal("modifier:ctrl", row3Key0.SpecialAction);
    }
}
