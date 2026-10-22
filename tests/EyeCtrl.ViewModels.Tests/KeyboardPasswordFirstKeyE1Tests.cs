namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;

// The FIRST E1 key line after leaving a password field must not describe the password: E1 records a
// key in ProcessKeyGaze BEFORE KeyDwellCompleted runs the handler, so the sensitivity guard has to
// run before E1 reads the mirror. Drives the real ProcessKeyGaze path (the review-01 probe PW2).
public class KeyboardPasswordFirstKeyE1Tests
{
    private const int DwellMs = 450, CooldownMs = 400, StepMs = 30;
    private static readonly string[] Ids = ["key_0_0", "key_0_1", "key_0_2", "backspace", "space"];
    private static readonly Dictionary<string, string> Outputs = new() { ["key_0_0"] = "h", ["key_0_1"] = "a", ["key_0_2"] = "x" };

    private sealed class Capture : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
        { lock (Lines) Lines.Add(f(s, x)); }
    }

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => [];
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly KeyboardViewModel Vm;
        public readonly Capture Log = new();
        public readonly FakeSensitive Sensitive = new();
        public readonly PredictionOrchestrator Orch = new(new WordPredictor(new InMemoryDictProvider()));
        public readonly Dictionary<string, PhysicalPoint> P =
            Ids.Select((id, i) => (id, p: new PhysicalPoint(100 + 220 * i, 100))).ToDictionary(t => t.id, t => t.p);
        public readonly Dictionary<string, PhysicalRect> B =
            Ids.Select((id, i) => (id, r: new PhysicalRect(220 * i, 0, 200, 200))).ToDictionary(t => t.id, t => t.r);
        public long Ts = 1_000_000;

        public Rig()
        {
            Vm = new KeyboardViewModel(new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                new WeakReferenceMessenger(), sensitiveContext: Sensitive);
            Vm.UseE1Recorder(new KeyboardE1Recorder(Log, () => Ts / 1000));
            Vm.SetOrchestrator(Orch);
            Vm.Configure(DwellMs, CooldownMs);
            Vm.SetLayout(new KeyboardLayout
            {
                Name = "T", DefaultSubLayout = "letters",
                SubLayouts = new Dictionary<string, SubLayout>
                {
                    ["letters"] = new()
                    {
                        Label = "ABC",
                        Rows = [[
                            new KeyDefinition { Label = "h", Output = "h" },
                            new KeyDefinition { Label = "a", Output = "a" },
                            new KeyDefinition { Label = "x", Output = "x" },
                            new KeyDefinition { Label = "<", SpecialAction = "backspace" },
                            new KeyDefinition { Label = "_", SpecialAction = "space" },
                        ]],
                    },
                },
            });
            Vm.KeyDwellCompleted += id =>
            {
                if (Outputs.TryGetValue(id, out var o)) Vm.ActivateKey(o, null);
                else if (id == "backspace") Vm.HandleBackspace();
                else if (id == "space") Vm.HandleSpace();
            };
        }

        public void Hold(PhysicalPoint g, int ms, bool inside = true)
        {
            var until = Ts + ms * 1000L;
            while (Ts <= until) { Vm.ProcessKeyGaze(P, g, inside, Ts, B); Ts += StepMs * 1000L; }
        }

        public void Type(string id) { Hold(P[id], DwellMs); Hold(P[id], CooldownMs + 60); }
        public void Dispose() => Vm.Dispose();
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split(' ').Skip(2).Where(t => t.Contains('=')).Select(t => t.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    // E1: the FIRST key line after leaving the field must not describe the password's mirror.
    // The line is recorded in ProcessKeyGaze BEFORE KeyDwellCompleted runs the handler's guard.
    [Fact]
    public void PW2_TheFirstKeyLineAfterLeaving_DoesNotSayAWordWasOpen()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("key_0_0"); rig.Type("key_0_1"); rig.Type("key_0_2"); rig.Type("key_0_1");
        rig.Sensitive.IsSensitiveActive = false;
        rig.Type("backspace");
        var line = rig.Log.Lines.Single(l => l.StartsWith("KB-E1 key ", StringComparison.Ordinal));
        var f = Fields(line);
        Check.WithCustomMessage("the first key line after the password: " + line).That(f["typing"]).IsEqualTo("False");
        Check.WithCustomMessage("the first key line after the password: " + line).That(f["bs"]).IsEqualTo("none");
    }
}
