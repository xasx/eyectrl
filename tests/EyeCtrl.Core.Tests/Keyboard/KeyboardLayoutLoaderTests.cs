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

            Check.That(layout.Name).IsEqualTo("QWERTY");
            Check.That(layout.DefaultSubLayout).IsEqualTo("letters");
            Check.That(layout.SubLayouts.Count).IsEqualTo(2);
            Check.That(layout.SubLayouts.ContainsKey("letters")).IsTrue();
            Check.That(layout.SubLayouts.ContainsKey("numbers")).IsTrue();

            var letters = layout.SubLayouts["letters"];
            Check.That(letters.Label).IsEqualTo("ABC");
            Check.That(letters.Rows.Count).IsEqualTo(3);
            Check.That(letters.Rows[0][0].Label).IsEqualTo("Q");
            Check.That(letters.Rows[0][0].Output).IsEqualTo("q");
            Check.That(letters.Rows[0][0].ShiftOutput).IsEqualTo("Q");

            // Switch button
            var switchKey = letters.Rows[2][0];
            Check.That(switchKey.SpecialAction).IsEqualTo("switchSub:numbers");

            var numbers = layout.SubLayouts["numbers"];
            Check.That(numbers.Label).IsEqualTo("123");
            Check.That(numbers.Rows.Count).IsEqualTo(2);
            Check.That(numbers.Rows[0][0].Label).IsEqualTo("1");
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
            Check.That(letters.Rows.Count).IsEqualTo(4);

            Check.That(letters.Rows[0][0].SpecialAction).IsEqualTo("modifier:win");
            Check.That(letters.Rows[1][0].SpecialAction).IsEqualTo("toggle:capslock");
            Check.That(letters.Rows[2][0].SpecialAction).IsEqualTo("modifier:alt");
            Check.That(letters.Rows[2][1].SpecialAction).IsEqualTo("modifier:shift");
            Check.That(letters.Rows[3][0].SpecialAction).IsEqualTo("modifier:ctrl");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_ParsesFunctionKeysSubLayout()
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
                  { "label": "Q", "output": "q", "shiftOutput": "Q" },
                  { "label": "Fn", "output": "", "width": 1.5, "specialAction": "switchSub:function" }
                ]
              ]
            },
            "function": {
              "label": "Fn",
              "rows": [
                [
                  { "label": "F1", "output": "", "width": 1.0, "specialAction": "key:F1" },
                  { "label": "F2", "output": "", "width": 1.0, "specialAction": "key:F2" },
                  { "label": "F3", "output": "", "width": 1.0, "specialAction": "key:F3" },
                  { "label": "F4", "output": "", "width": 1.0, "specialAction": "key:F4" },
                  { "label": "F5", "output": "", "width": 1.0, "specialAction": "key:F5" },
                  { "label": "F6", "output": "", "width": 1.0, "specialAction": "key:F6" }
                ],
                [
                  { "label": "F7", "output": "", "width": 1.0, "specialAction": "key:F7" },
                  { "label": "F8", "output": "", "width": 1.0, "specialAction": "key:F8" },
                  { "label": "F9", "output": "", "width": 1.0, "specialAction": "key:F9" },
                  { "label": "F10", "output": "", "width": 1.0, "specialAction": "key:F10" },
                  { "label": "F11", "output": "", "width": 1.0, "specialAction": "key:F11" },
                  { "label": "F12", "output": "", "width": 1.0, "specialAction": "key:F12" }
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

            Check.That(layout.Name).IsEqualTo("QWERTY");
            Check.That(layout.SubLayouts.Count).IsEqualTo(2);
            Check.That(layout.SubLayouts.ContainsKey("function")).IsTrue();

            var functionLayout = layout.SubLayouts["function"];
            Check.That(functionLayout.Label).IsEqualTo("Fn");
            Check.That(functionLayout.Rows.Count).IsEqualTo(3);

            // Verify first row (F1-F6)
            Check.That(functionLayout.Rows[0].Count).IsEqualTo(6);
            Check.That(functionLayout.Rows[0][0].Label).IsEqualTo("F1");
            Check.That(functionLayout.Rows[0][0].SpecialAction).IsEqualTo("key:F1");
            Check.That(functionLayout.Rows[0][5].Label).IsEqualTo("F6");
            Check.That(functionLayout.Rows[0][5].SpecialAction).IsEqualTo("key:F6");

            // Verify second row (F7-F12)
            Check.That(functionLayout.Rows[1].Count).IsEqualTo(6);
            Check.That(functionLayout.Rows[1][0].Label).IsEqualTo("F7");
            Check.That(functionLayout.Rows[1][0].SpecialAction).IsEqualTo("key:F7");
            Check.That(functionLayout.Rows[1][5].Label).IsEqualTo("F12");
            Check.That(functionLayout.Rows[1][5].SpecialAction).IsEqualTo("key:F12");

            // Verify switch back button
            Check.That(functionLayout.Rows[2][0].SpecialAction).IsEqualTo("switchSub:letters");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
