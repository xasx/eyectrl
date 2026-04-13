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
    public void Load_ParsesModifierSpecialActions()
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
                  { "label": "Win", "specialAction": "modifier:win" },
                  { "label": "Q", "output": "q", "shiftOutput": "Q" }
                ],
                [
                  { "label": "Caps", "specialAction": "toggle:capslock" },
                  { "label": "A", "output": "a", "shiftOutput": "A" }
                ],
                [
                  { "label": "Alt", "specialAction": "modifier:alt" },
                  { "label": "Shift", "specialAction": "modifier:shift" },
                  { "label": "Z", "output": "z", "shiftOutput": "Z" }
                ],
                [
                  { "label": "Ctrl", "specialAction": "modifier:ctrl" },
                  { "label": "Space", "output": " " }
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

            var letters = layout.SubLayouts["letters"];
            Assert.Equal(4, letters.Rows.Count);

            Assert.Equal("modifier:win", letters.Rows[0][0].SpecialAction);
            Assert.Equal("toggle:capslock", letters.Rows[1][0].SpecialAction);
            Assert.Equal("modifier:alt", letters.Rows[2][0].SpecialAction);
            Assert.Equal("modifier:shift", letters.Rows[2][1].SpecialAction);
            Assert.Equal("modifier:ctrl", letters.Rows[3][0].SpecialAction);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
