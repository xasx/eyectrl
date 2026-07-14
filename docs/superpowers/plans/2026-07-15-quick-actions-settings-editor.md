# Quick Actions Settings Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a gaze-authorable CRUD editor to the Settings → Quick Actions tab so users can enable/disable/delete any action and create the three gaze-authorable types (keyboard shortcut, media control, launch app) — no free-text authoring, no dropdowns.

**Architecture:** A small in-tab view-state machine (`List → PickType → PickShortcut | PickMedia | PickApp`) drives `SettingsWindow.BuildQuickActionsPages`; every pick surface is a paginated grid of big `DwellButton`s reusing the existing `RebuildContent()`/pagination. Actions persist through the existing `IQuickActionsStore`. Installed apps come from a new Shell-`AppsFolder` COM enumerator behind an `IInstalledAppProvider` seam.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit + NFluent, Shell32 COM interop.

**Branch:** `feat/quick-actions-editor` (already checked out in the worktree; based on the merged Quick Actions toolbar work). Spec: `docs/superpowers/specs/2026-07-14-quick-actions-settings-editor-design.md`.

---

## File Structure

**New files**
- `src/EyeCtrl.Core/Models/InstalledApp.cs` — `InstalledApp(DisplayName, Aumid)` record struct.
- `src/EyeCtrl.Core/Interfaces/IInstalledAppProvider.cs` — app-enumeration seam.
- `src/EyeCtrl.Core/Models/QuickActionPresets.cs` — keyboard + media preset tables.
- `src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs` — Shell `AppsFolder` COM enumerator (STA + Lazy).
- `tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs`
- `tests/EyeCtrl.Core.Tests/Models/QuickActionPresetsTests.cs`
- `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs`

**Modified files**
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — register `IInstalledAppProvider`; drop orphaned editor-VM singleton.
- `src/EyeCtrl.App/Services/IEventCoordinator.cs` — append `IInstalledAppProvider` to `EventCoordinationContext`.
- `src/EyeCtrl.App/App.xaml.cs` — pass provider into the context; prewarm it.
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — pass provider into the editor VM + call `Load()`.
- `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs` — new ctor param + typed `Add*`; remove `AddAction`/`UpdateAction`.
- `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — the view-state editor.
- `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs` — media dispatch test + context ctor propagation.
- `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` — editor-VM ctor propagation.

**Task order (dependencies):** 1 (types) → 2 (provider) → 3 (DI/context wiring) → 4 (presets) → 5 (media test) → 6 (editor VM) → 7 (settings UI).

---

### Task 1: `InstalledApp` model + `IInstalledAppProvider` seam

**Files:**
- Create: `src/EyeCtrl.Core/Models/InstalledApp.cs`
- Create: `src/EyeCtrl.Core/Interfaces/IInstalledAppProvider.cs`

- [ ] **Step 1: Write the model**

```csharp
// src/EyeCtrl.Core/Models/InstalledApp.cs
namespace EyeCtrl.Core.Models;

/// <summary>A launchable installed app: friendly display name + its AppUserModelID (AUMID).</summary>
public readonly record struct InstalledApp(string DisplayName, string Aumid);
```

- [ ] **Step 2: Write the interface**

```csharp
// src/EyeCtrl.Core/Interfaces/IInstalledAppProvider.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>Enumerates installed apps (packaged AUMIDs) for the Quick Actions app picker.</summary>
public interface IInstalledAppProvider
{
    /// <summary>Returns installed apps; may be empty if enumeration is unavailable. Never throws.</summary>
    IReadOnlyList<InstalledApp> GetInstalledApps();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/EyeCtrl.Core --no-restore`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Models/InstalledApp.cs src/EyeCtrl.Core/Interfaces/IInstalledAppProvider.cs
git commit -m "feat(quick-actions): add InstalledApp model + IInstalledAppProvider seam"
```

---

### Task 2: `Win32InstalledAppProvider` (Shell AppsFolder COM)

**Files:**
- Create: `src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs`
- Test: `tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs`

- [ ] **Step 1: Write the smoke test**

```csharp
// tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs
namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

public class Win32InstalledAppProviderTests
{
    [Fact]
    public void GetInstalledApps_NeverThrows_AndEntriesAreWellFormed()
    {
        var provider = new Win32InstalledAppProvider();

        // Must not throw even where the shell namespace is unavailable (headless CI).
        var apps = provider.GetInstalledApps();

        Check.That(apps).IsNotNull();
        // Empty is a valid result (minimal images). Assert shape only when present.
        foreach (var app in apps)
        {
            Check.That(app.DisplayName).IsNotEmpty();
            Check.That(app.Aumid).Contains("!");
        }
    }

    [Fact]
    public void GetInstalledApps_IsCached_ReturnsSameInstanceOnRepeatCalls()
    {
        var provider = new Win32InstalledAppProvider();
        Check.That(provider.GetInstalledApps()).IsSameReferenceAs(provider.GetInstalledApps());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~Win32InstalledAppProviderTests"`
Expected: FAIL — `Win32InstalledAppProvider` does not exist.

- [ ] **Step 3: Write the provider**

AUMIDs come from `IShellItem.GetDisplayName(SIGDN_PARENTRELATIVEPARSING)` (for `AppsFolder` children this is the AUMID), so no `IShellItem2`/`PROPERTYKEY` interop is needed. Shell COM requires STA, so the walk runs on a dedicated STA thread; the `Lazy` factory swallows failures into an empty list (never a cached exception).

```csharp
// src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs
namespace EyeCtrl.Platform.Services;

using System.Runtime.InteropServices;
using System.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Enumerates launchable apps from the Shell "AppsFolder" via Shell32 COM, keeping packaged apps
/// (AUMID contains '!') that <c>IApplicationActivationManager.ActivateApplication</c> can launch.
/// The walk runs on a dedicated STA thread (Shell COM requires STA) and is cached once per process;
/// any failure yields an empty list (never a thrown or cached exception).
/// </summary>
public sealed class Win32InstalledAppProvider : IInstalledAppProvider
{
    private readonly Lazy<IReadOnlyList<InstalledApp>> _cache =
        new(EnumerateOnStaThread, LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<InstalledApp> GetInstalledApps() => _cache.Value;

    private static IReadOnlyList<InstalledApp> EnumerateOnStaThread()
    {
        IReadOnlyList<InstalledApp> result = Array.Empty<InstalledApp>();
        var t = new Thread(() =>
        {
            try { result = Enumerate(); }
            catch { result = Array.Empty<InstalledApp>(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static IReadOnlyList<InstalledApp> Enumerate()
    {
        var apps = new List<InstalledApp>();

        var shellItemIid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref shellItemIid, out var folderObj) != 0
            || folderObj is not IShellItem folder)
            return apps;

        try
        {
            var bhid = BHID_EnumItems;
            var enumIid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumObj) != 0
                || enumObj is not IEnumShellItems items)
                return apps;

            try
            {
                var buffer = new IShellItem[1];
                while (items.Next(1, buffer, out var fetched) == 0 && fetched == 1)
                {
                    var item = buffer[0];
                    try
                    {
                        var name = GetDisplayName(item, SIGDN.NORMALDISPLAY);
                        var aumid = GetDisplayName(item, SIGDN.PARENTRELATIVEPARSING);
                        if (name.Length > 0 && aumid.Contains('!'))
                            apps.Add(new InstalledApp(name, aumid));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }

        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string GetDisplayName(IShellItem item, SIGDN sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0 || ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    // ── COM interop ──
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt,
            out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~Win32InstalledAppProviderTests"`
Expected: 2 tests PASS (on a dev box the list is non-empty and every entry is well-formed; on a headless runner an empty list still passes).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs
git commit -m "feat(quick-actions): add Win32InstalledAppProvider (Shell AppsFolder, STA, cached)"
```

---

### Task 3: Wire `IInstalledAppProvider` through DI + context (unused yet)

Registers the provider, threads it through `EventCoordinationContext`, prewarms it, and updates every context constructor (App + tests) so the solution compiles with the provider available. Nothing consumes it yet.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Modify: `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs`

- [ ] **Step 1: Register the provider in `AppBootstrapper`**

In `AppBootstrapper.cs`, immediately after the `IWindowsAppLauncher` registration (the line `services.AddSingleton<IWindowsAppLauncher, WindowsAppLauncher>();`), add:

```csharp
        services.AddSingleton<IInstalledAppProvider, Win32InstalledAppProvider>();
```

Add `using EyeCtrl.Platform.Services;` at the top if not already present (the file already registers other `Win32*`/Platform services, so it likely is). Confirm `using EyeCtrl.Core.Interfaces;` is present for `IInstalledAppProvider`.

- [ ] **Step 2: Append the provider to `EventCoordinationContext`**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, the record currently ends:

```csharp
    IWindowManager WindowManager,
    QuickActionsGridViewModel QuickActionsGridVM,
    IQuickActionsStore QuickActionsStore,
    IWindowsAppLauncher WindowsAppLauncher);
```

Change the last line to append the provider (keep append-at-end discipline):

```csharp
    IWindowsAppLauncher WindowsAppLauncher,
    IInstalledAppProvider InstalledAppProvider);
```

Ensure `using EyeCtrl.Core.Interfaces;` is present in that file.

- [ ] **Step 3: Pass the provider in `App.xaml.cs` + prewarm**

In `App.xaml.cs`, the `EventCoordinationContext` is constructed positionally; its last argument is currently `_serviceProvider.GetRequiredService<IWindowsAppLauncher>()`. Append after it:

```csharp
                _serviceProvider.GetRequiredService<IWindowsAppLauncher>(),
                _serviceProvider.GetRequiredService<IInstalledAppProvider>());
```

Then, after the service provider is built and the coordinator is initialized (anywhere `_serviceProvider` is in scope in `OnStartup`), add a fire-and-forget prewarm so the first PickApp doesn't stall the UI thread:

```csharp
            // Warm the installed-app cache off the UI thread (the provider walks Shell AppsFolder
            // on its own STA worker; this just triggers the one-time enumeration early).
            System.Threading.Tasks.Task.Run(
                () => _serviceProvider.GetRequiredService<IInstalledAppProvider>().GetInstalledApps());
```

- [ ] **Step 4: Fix the existing `EventCoordinatorTests` context constructions**

`EventCoordinatorTests.cs` builds a full `new EventCoordinationContext(...)` in the keyboard-shortcut and enabled-setting tests. Each now needs the trailing `InstalledAppProvider` argument. Add a fake near the other test fakes:

```csharp
    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }
```

Then append `new FakeInstalledAppProvider()` as the final argument to every `new EventCoordinationContext(...)` in the file (search for `new EventCoordinationContext(`). Add `using EyeCtrl.Core.Models;` if not already imported.

- [ ] **Step 5: Build and run the full test suite (headless-safe subset)**

Run: `dotnet build --no-restore`
Expected: build succeeds.
Run: `dotnet test --filter "Category!=UI"`
Expected: all currently-passing tests still pass (no behavior change).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs
git commit -m "feat(quick-actions): register + thread IInstalledAppProvider through DI/context"
```

---

### Task 4: `QuickActionPresets` + keyboard-chord validation test

**Files:**
- Create: `src/EyeCtrl.Core/Models/QuickActionPresets.cs`
- Test: `tests/EyeCtrl.Core.Tests/Models/QuickActionPresetsTests.cs`

- [ ] **Step 1: Write the failing test**

The keyboard test mirrors `ExecuteKeyboardShortcut`'s resolution order: single-char keys via `Win32InputSimulator.CharToVirtualKey(lowercased)`, then `KeyNameMapper.TryMap`. `Win+D/E/L` are single-char and resolve through `CharToVirtualKey`.

```csharp
// tests/EyeCtrl.Core.Tests/Models/QuickActionPresetsTests.cs
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

public class QuickActionPresetsTests
{
    [Fact]
    public void KeyboardShortcuts_AreNonEmpty_AndHaveUniqueLabels()
    {
        var labels = QuickActionPresets.KeyboardShortcuts.Select(p => p.Label).ToList();
        Check.That(labels).Not.IsEmpty();
        Check.That(labels.Distinct().Count()).IsEqualTo(labels.Count);
    }

    [Fact]
    public void EveryKeyboardPreset_TerminalKey_ResolvesToNonZeroVk()
    {
        foreach (var (label, chord) in QuickActionPresets.KeyboardShortcuts)
        {
            var parts = chord.Split('+');
            var key = parts[^1].Trim();

            ushort vk = 0;
            if (key.Length == 1)
            {
                var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
                (vk, _) = Win32InputSimulator.CharToVirtualKey(ch);
            }
            if (vk == 0 && KeyNameMapper.TryMap(key, out var mappedVk, out _))
                vk = mappedVk;

            Check.That(vk).As($"preset '{label}' chord '{chord}'").IsNotEqualTo((ushort)0);
        }
    }

    [Fact]
    public void MediaControls_CommandsAreTheCanonicalSet()
    {
        // Guards the preset table against a typo; the executor-side dispatch is checked in
        // EventCoordinatorTests (ExecuteMediaControl is private to the App layer).
        var expected = new[] { "PlayPause", "Stop", "NextTrack", "PreviousTrack", "VolumeUp", "VolumeDown", "VolumeMute" };
        var actual = QuickActionPresets.MediaControls.Select(m => m.Command).ToList();
        Check.That(actual).ContainsExactly(expected);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionPresetsTests"`
Expected: FAIL — `QuickActionPresets` does not exist.

> Note: `EyeCtrl.Core.Tests` already references `EyeCtrl.Platform` (existing tests use `Win32InputSimulator.CharToVirtualKey`). If the reference is missing, add it to the test csproj — check with `grep -n "EyeCtrl.Platform" tests/EyeCtrl.Core.Tests/*.csproj` before assuming.

- [ ] **Step 3: Write the presets**

```csharp
// src/EyeCtrl.Core/Models/QuickActionPresets.cs
namespace EyeCtrl.Core.Models;

/// <summary>
/// Fixed preset tables for the Quick Actions settings editor. Data only — the editor turns a
/// chosen entry into a <see cref="QuickAction"/>. Keyboard chords must parse via the same path
/// <c>EventCoordinator.ExecuteKeyboardShortcut</c> uses; media commands must match the
/// <c>ExecuteMediaControl</c> switch (both covered by tests).
/// </summary>
public static class QuickActionPresets
{
    /// <summary>Common keyboard shortcuts: display label + chord string.</summary>
    public static IReadOnlyList<(string Label, string Chord)> KeyboardShortcuts { get; } = new[]
    {
        ("Copy", "Ctrl+C"),
        ("Paste", "Ctrl+V"),
        ("Cut", "Ctrl+X"),
        ("Undo", "Ctrl+Z"),
        ("Redo", "Ctrl+Y"),
        ("Select All", "Ctrl+A"),
        ("Save", "Ctrl+S"),
        ("Find", "Ctrl+F"),
        ("New", "Ctrl+N"),
        ("Print", "Ctrl+P"),
        ("Switch App", "Alt+Tab"),
        ("Show Desktop", "Win+D"),
        ("Lock", "Win+L"),
        ("File Explorer", "Win+E"),
    };

    /// <summary>Media transport commands: display label + payload command (matches ExecuteMediaControl).</summary>
    public static IReadOnlyList<(string Label, string Command)> MediaControls { get; } = new[]
    {
        ("Play / Pause", "PlayPause"),
        ("Stop", "Stop"),
        ("Next", "NextTrack"),
        ("Previous", "PreviousTrack"),
        ("Volume Up", "VolumeUp"),
        ("Volume Down", "VolumeDown"),
        ("Mute", "VolumeMute"),
    };
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionPresetsTests"`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/QuickActionPresets.cs tests/EyeCtrl.Core.Tests/Models/QuickActionPresetsTests.cs
git commit -m "feat(quick-actions): add QuickActionPresets + chord/command validation tests"
```

---

### Task 5: Media-preset dispatch test (App layer)

Verifies every `QuickActionPresets.MediaControls` command is actually handled by `EventCoordinator.ExecuteMediaControl` (private, App-only) — so a preset command can never silently no-op.

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs`

- [ ] **Step 1: Extend the fake input simulator to record plain key presses**

In `EventCoordinatorTests.FakeInputSimulator`, add a recorded field and set it in the `SendKeyPress(ushort, bool)` overload (media presses use the extended-key overload). Add near `LastModifiedKeyPress`:

```csharp
        public (ushort Vk, bool Extended)? LastKeyPress { get; private set; }
```

And change the `SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)` method body from empty to:

```csharp
        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)
        {
            LastKeyPress = (virtualKeyCode, isExtendedKey);
        }
```

(Leave the parameterless `SendKeyPress(ushort)` as-is.)

- [ ] **Step 2: Write the failing test**

Add this test method, mirroring the existing reflection-based invocation used by `ExecuteKeyboardShortcut` tests (there is already a private `InvokeExecuteKeyboardShortcut` helper and a `SetContext` helper — reuse the same pattern):

```csharp
    [Fact]
    public void ExecuteMediaControl_EveryPreset_ProducesAnExtendedKeyPress()
    {
        foreach (var (label, command) in EyeCtrl.Core.Models.QuickActionPresets.MediaControls)
        {
            var coordinator = new EventCoordinator();
            var input = new FakeInputSimulator();
            SetContext(coordinator, MakeContext(input));

            typeof(EventCoordinator)
                .GetMethod("ExecuteMediaControl", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(coordinator, new object[] { command });

            Check.That(input.LastKeyPress).As($"media preset '{label}' ({command})").IsNotNull();
            Check.That(input.LastKeyPress!.Value.Vk).As($"media preset '{label}'").IsNotEqualTo((ushort)0);
            Check.That(input.LastKeyPress.Value.Extended).IsTrue();
        }
    }
```

If the file does not already have a `MakeContext(IInputSimulator)` helper, extract one from the existing keyboard test's `new EventCoordinationContext(...)` block so both tests share it (it must include the `FakeInstalledAppProvider` trailing arg from Task 3). Keep `SetContext` as-is.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~EventCoordinatorTests"`
Expected: PASS — all 7 media presets dispatch to a non-zero extended key press (this confirms the preset table and `ExecuteMediaControl` agree).

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs
git commit -m "test(quick-actions): assert every media preset dispatches via ExecuteMediaControl"
```

---

### Task 6: Extend `SettingsQuickActionsEditorViewModel` + wire `Load()`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs`

- [ ] **Step 1: Write the failing editor-VM test**

```csharp
// tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public class SettingsQuickActionsEditorViewModelTests
{
    private sealed class FakeStore : IQuickActionsStore
    {
        public List<QuickAction> Saved { get; private set; } = new();
        public IReadOnlyList<QuickAction> LoadAll() => Saved;
        public void Save(IReadOnlyList<QuickAction> actions) => Saved = actions.ToList();
        public QuickAction? FindById(string id) => Saved.FirstOrDefault(a => a.Id == id);
    }

    private sealed class FakeApps : IInstalledAppProvider
    {
        private readonly IReadOnlyList<InstalledApp> _apps;
        public FakeApps(params InstalledApp[] apps) => _apps = apps;
        public IReadOnlyList<InstalledApp> GetInstalledApps() => _apps;
    }

    private static SettingsQuickActionsEditorViewModel Vm(FakeStore store, IInstalledAppProvider? apps = null)
        => new(store, apps ?? new FakeApps());

    [Fact]
    public void AddKeyboardShortcut_CreatesAndPersistsAction()
    {
        var store = new FakeStore();
        var vm = Vm(store);
        vm.AddKeyboardShortcut("Copy", "Ctrl+C");

        Check.That(vm.Actions).HasSize(1);
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(store.Saved[0].Label).IsEqualTo("Copy");
        Check.That(store.Saved[0].Payload).IsEqualTo("Ctrl+C");
        Check.That(store.Saved[0].Enabled).IsTrue();
    }

    [Fact]
    public void AddMediaControl_CreatesMediaAction()
    {
        var store = new FakeStore();
        Vm(store).AddMediaControl("Play / Pause", "PlayPause");
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.MediaControl);
        Check.That(store.Saved[0].Payload).IsEqualTo("PlayPause");
    }

    [Fact]
    public void AddApp_CreatesLaunchWindowsAppWithAumidAndName()
    {
        var store = new FakeStore();
        Vm(store).AddApp(new InstalledApp("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.LaunchWindowsApp);
        Check.That(store.Saved[0].Label).IsEqualTo("Calculator");
        Check.That(store.Saved[0].Payload).IsEqualTo("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
    }

    [Fact]
    public void ToggleEnabled_And_Delete_PersistById()
    {
        var store = new FakeStore();
        var vm = Vm(store);
        vm.AddMediaControl("Mute", "VolumeMute");
        var id = vm.Actions[0].Id;

        vm.ToggleEnabled(id);
        Check.That(store.Saved[0].Enabled).IsFalse();

        vm.DeleteAction(id);
        Check.That(vm.Actions).IsEmpty();
        Check.That(store.Saved).IsEmpty();
    }

    [Fact]
    public void Load_PopulatesFromStore_IncludingDisabled()
    {
        var store = new FakeStore();
        store.Save(new[]
        {
            new QuickAction { Type = QuickActionType.CannedText, Label = "Hi", Payload = "hi", Enabled = false },
        });
        var vm = Vm(store);
        vm.Load();
        Check.That(vm.Actions).HasSize(1);
        Check.That(vm.Actions[0].Enabled).IsFalse();
    }

    [Fact]
    public void InstalledApps_ComesFromProvider()
    {
        var apps = new FakeApps(new InstalledApp("A", "a!x"), new InstalledApp("B", "b!x"));
        Check.That(Vm(new FakeStore(), apps).InstalledApps).HasSize(2);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsQuickActionsEditorViewModelTests"`
Expected: FAIL — new ctor/methods don't exist.

- [ ] **Step 3: Update the editor VM**

Replace the ctor and the `AddAction`/`UpdateAction` methods. The full file becomes:

```csharp
// src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Editor ViewModel for the Quick Actions settings tab. Loads every <see cref="QuickAction"/>
/// (including disabled) from the store and provides gaze-authorable create + manage operations.
/// </summary>
public partial class SettingsQuickActionsEditorViewModel : ObservableObject
{
    private readonly IQuickActionsStore _store;
    private readonly IInstalledAppProvider _appProvider;

    /// <summary>All stored actions (enabled and disabled) for the editor list.</summary>
    public ObservableCollection<QuickAction> Actions { get; } = new();

    /// <summary>Installed apps for the "Launch App" picker (provider-cached).</summary>
    public IReadOnlyList<InstalledApp> InstalledApps => _appProvider.GetInstalledApps();

    public SettingsQuickActionsEditorViewModel(IQuickActionsStore store, IInstalledAppProvider appProvider)
    {
        _store = store;
        _appProvider = appProvider;
    }

    /// <summary>Reloads every action from the store into <see cref="Actions"/>.</summary>
    public void Load()
    {
        Actions.Clear();
        foreach (var a in _store.LoadAll())
            Actions.Add(a);
    }

    /// <summary>Persists the current <see cref="Actions"/> collection.</summary>
    public void Save() => _store.Save(Actions.ToList());

    /// <summary>Adds a keyboard-shortcut action (payload is the chord string) and saves.</summary>
    public void AddKeyboardShortcut(string label, string chord)
        => Add(QuickActionType.KeyboardShortcut, label, chord);

    /// <summary>Adds a media-control action (payload is the command string) and saves.</summary>
    public void AddMediaControl(string label, string command)
        => Add(QuickActionType.MediaControl, label, command);

    /// <summary>Adds a launch-Windows-app action (payload is the AUMID; label is the app name) and saves.</summary>
    public void AddApp(InstalledApp app)
        => Add(QuickActionType.LaunchWindowsApp, app.DisplayName, app.Aumid);

    private void Add(QuickActionType type, string label, string payload)
    {
        Actions.Add(new QuickAction { Type = type, Label = label, Payload = payload });
        Save();
    }

    /// <summary>Removes the action with the given id (if present) and saves.</summary>
    public void DeleteAction(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            Actions.Remove(action);
            Save();
        }
    }

    /// <summary>Toggles the <see cref="QuickAction.Enabled"/> flag of the action with the given id and saves.</summary>
    public void ToggleEnabled(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            action.Enabled = !action.Enabled;
            Save();
        }
    }
}
```

(Note: removed `AddAction(QuickActionType)`, `UpdateAction(...)`, the unused `IsVisible` observable property, and the `CommunityToolkit.Mvvm.Messaging` using — none have callers.)

- [ ] **Step 4: Update `ShowSettingsWindow` to pass the provider + call `Load()`**

In `EventCoordinator.cs` `ShowSettingsWindow`, change the editor construction:

```csharp
            var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
                _context.QuickActionsStore, _context.InstalledAppProvider);
            quickActionsEditor.Load();
```

- [ ] **Step 5: Remove the orphaned editor-VM DI registration**

In `AppBootstrapper.cs`, delete the dead line `services.AddSingleton<SettingsQuickActionsEditorViewModel>();` (the editor VM is only ever `new`'d in `ShowSettingsWindow`; nothing resolves it from DI).

- [ ] **Step 6: Fix `SettingsWindowViewModelTests` ctor propagation**

That test constructs the editor VM directly, e.g. `new SettingsQuickActionsEditorViewModel(new FakeQuickActionsStore())`. Add a fake provider and pass it:

```csharp
    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }
```

and change the construction to `new SettingsQuickActionsEditorViewModel(new FakeQuickActionsStore(), new FakeInstalledAppProvider())`. Add `using EyeCtrl.Core.Interfaces;` / `using EyeCtrl.Core.Models;` if missing.

- [ ] **Step 7: Build and run tests**

Run: `dotnet build --no-restore`
Expected: build succeeds (editor VM, EventCoordinator, both test projects compile).
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsQuickActionsEditorViewModelTests|FullyQualifiedName~SettingsWindowViewModelTests"`
Expected: editor VM tests PASS; SettingsWindowViewModel tests still PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs
git commit -m "feat(quick-actions): typed Add* on editor VM + Load() on settings open"
```

---

### Task 7: Settings tab view-state editor UI

Rewrites `BuildQuickActionsPages` into a `List → PickType → PickShortcut | PickMedia | PickApp` state machine of big dwell buttons, and resets the view on tab change. This is a WPF task — no unit test; verified by build + a manual `--test-mode` run.

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

- [ ] **Step 1: Add the view-state field, enum, and transition helper**

Near the other private state fields (after `private List<List<Action>> _pages = new();`), add:

```csharp
    private enum QaView { List, PickType, PickShortcut, PickMedia, PickApp }
    private QaView _qaView = QaView.List;

    // Every Quick Actions view transition starts the view at its first page, then rebuilds.
    // Never reset CurrentPage inside BuildQuickActionsPages — it would fight pag_prev/pag_next.
    private void GoToQaView(QaView view)
    {
        _qaView = view;
        _viewModel.Settings.CurrentPage = 0;
        RebuildContent();
    }
```

- [ ] **Step 2: Reset the view on tab change**

In `OnSettingsViewModelPropertyChanged`, change the `CurrentTabName` case so leaving/entering any tab resets the Quick Actions view to `List` (this fires on every tab switch, including re-entering Quick Actions):

```csharp
                case nameof(SettingsViewModel.CurrentTabName):
                    _qaView = QaView.List;
                    _viewModel.Settings.CurrentPage = 0;
                    RebuildContent();
                    break;
```

- [ ] **Step 3: Replace `BuildQuickActionsPages` with the view-state dispatcher + per-view builders**

Replace the entire current `BuildQuickActionsPages()` method with the following. **Glyph note:** all editor glyphs used here (⌨ ♫ ⊞ ▦ ⚡ ← ✕ ＋) are BMP characters, safe as C# source literals. (The launch-app editor glyph is `▦`, not the non-BMP 🪟 the *runtime grid tiles* use — those live in `QuickActionTileViewModel` and are unchanged by this plan.)

```csharp
    private const int QaActionsPerPage = 4;      // action rows per List page (pages 2+)
    private const int QaPresetsPerPage = 6;      // preset/app buttons per picker page

    private static string QaTypeGlyph(QuickActionType t) => t switch
    {
        QuickActionType.KeyboardShortcut => "⌨",       // ⌨
        QuickActionType.CannedText => "T",
        QuickActionType.LaunchExe => "⊞",              // ⊞
        QuickActionType.LaunchWindowsApp => "▦",       // BMP launch-app glyph (grid tiles use 🪟 separately)
        QuickActionType.LaunchUrl => "@",
        QuickActionType.MediaControl => "♫",           // ♫
        _ => "⚡",                                      // ⚡
    };

    private List<List<Action>> BuildQuickActionsPages() => _qaView switch
    {
        QaView.PickType => BuildQaTypePickerPages(),
        QaView.PickShortcut => BuildQaShortcutPages(),
        QaView.PickMedia => BuildQaMediaPages(),
        QaView.PickApp => BuildQaAppPages(),
        _ => BuildQaListPages(),
    };

    private List<List<Action>> BuildQaListPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var qa = _settings.QuickActions;
        var editor = _viewModel.QuickActionsEditor;

        var pages = new List<List<Action>>();

        // Page 1 (pinned): config controls + "Add action".
        pages.Add(new List<Action>
        {
            () => Add(new DwellToggle(_viewModel, "qa_enabled", "Enable Quick Actions",
                () => qa.Enabled, v => { qa.Enabled = v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellComboBox(_viewModel, "qa_edge", "Summon Edge",
                new[] { "Left", "Right", "Top", "Bottom" },
                () => qa.SummonEdge, v => { qa.SummonEdge = v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_summon", "Summon Threshold (ms)",
                200, 2000, 50, 0,
                () => qa.SummonThresholdMs, v => { qa.SummonThresholdMs = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_dismiss", "Dismiss Timeout (ms)",
                1000, 15000, 500, 0,
                () => qa.DismissTimeoutMs, v => { qa.DismissTimeoutMs = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_columns", "Grid Columns",
                2, 5, 1, 0,
                () => qa.GridColumns, v => { qa.GridColumns = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellButton(_viewModel, "qa_add", "＋  Add action",
                () => GoToQaView(QaView.PickType), ModalYes, cat, r, _dwellMs, width: 360, height: 80)),
        });

        // Pages 2+: one row per action (enable toggle + delete), chunked.
        var actions = editor.Actions.ToList();
        for (int i = 0; i < actions.Count; i += QaActionsPerPage)
        {
            var chunk = actions.Skip(i).Take(QaActionsPerPage).ToList();
            var page = new List<Action>();
            foreach (var action in chunk)
            {
                var a = action; // capture per iteration
                var caption = $"{QaTypeGlyph(a.Type)}  {a.Label}";
                page.Add(() => Add(new DwellToggle(_viewModel, "qa_en_" + a.Id, caption,
                    () => a.Enabled, _ => { editor.ToggleEnabled(a.Id); }, cat, r, _dwellMs)));
                page.Add(() => Add(new DwellButton(_viewModel, "qa_del_" + a.Id, "✕  Delete",
                    () => ShowModal($"Delete \"{a.Label}\"?", () => { editor.DeleteAction(a.Id); RebuildContent(); }),
                    DeleteBase, cat, r, _dwellMs, width: 360, height: 70)));
            }
            pages.Add(page);
        }

        return pages;
    }

    private List<Action> QaBackButton(int page) => new()
    {
        () => Add(new DwellButton(_viewModel, "qa_back_" + page, "←  Back",
            () => GoToQaView(QaView.List), ResetBase,
            DwellCategory.Control, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs,
            width: 360, height: 70)),
    };

    private List<List<Action>> BuildQaTypePickerPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        var page = QaBackButton(0);
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_kbd", "⌨  Keyboard Shortcut",
            () => GoToQaView(QaView.PickShortcut), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_media", "♫  Media Control",
            () => GoToQaView(QaView.PickMedia), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_app", "▦  Launch App",
            () => GoToQaView(QaView.PickApp), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        return new List<List<Action>> { page };
    }

    private List<List<Action>> BuildQaShortcutPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var editor = _viewModel.QuickActionsEditor;

        return ChunkPickerPages(QuickActionPresets.KeyboardShortcuts.Count, "qa_ks", i =>
        {
            var (label, chord) = QuickActionPresets.KeyboardShortcuts[i];
            return (label, () => { editor.AddKeyboardShortcut(label, chord); GoToQaView(QaView.List); });
        });
    }

    private List<List<Action>> BuildQaMediaPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var editor = _viewModel.QuickActionsEditor;

        return ChunkPickerPages(QuickActionPresets.MediaControls.Count, "qa_md", i =>
        {
            var (label, command) = QuickActionPresets.MediaControls[i];
            return (label, () => { editor.AddMediaControl(label, command); GoToQaView(QaView.List); });
        });
    }

    private List<List<Action>> BuildQaAppPages()
    {
        var editor = _viewModel.QuickActionsEditor;
        var apps = editor.InstalledApps;
        if (apps.Count == 0)
        {
            var empty = QaBackButton(0);
            empty.Insert(0, () => Add(new DwellLabel("No installed apps found.")));
            return new List<List<Action>> { empty };
        }
        return ChunkPickerPages(apps.Count, "qa_app", i =>
        {
            var app = apps[i];
            return (app.DisplayName, () => { editor.AddApp(app); GoToQaView(QaView.List); });
        });
    }

    // Builds paginated picker pages of big dwell buttons; every page starts with a Back button.
    private List<List<Action>> ChunkPickerPages(int count, string idPrefix, Func<int, (string Label, Action OnPick)> itemAt)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var pages = new List<List<Action>>();

        for (int start = 0, page = 0; start < count; start += QaPresetsPerPage, page++)
        {
            var actions = QaBackButton(page);
            for (int i = start; i < Math.Min(start + QaPresetsPerPage, count); i++)
            {
                var (label, onPick) = itemAt(i);
                var id = $"{idPrefix}_{i}";
                actions.Add(() => Add(new DwellButton(_viewModel, id, label, onPick,
                    BtnBase, cat, r, _dwellMs, width: 460, height: 80)));
            }
            pages.Add(actions);
        }
        return pages;
    }
```

Add `using EyeCtrl.Core.Models;` at the top of `SettingsWindow.xaml.cs` if not already present (needed for `QuickActionPresets` and `QuickActionType`).

- [ ] **Step 4: Build**

Run: `dotnet build src/EyeCtrl.App --no-restore`
Expected: build succeeds, no warnings.

- [ ] **Step 5: Manual verification**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`
Then, in the app:
- Open Settings → **Quick Actions**. Page 1 shows the 5 config controls + **＋ Add action**; if `actions.json` has entries they appear as toggle + delete rows on later pages.
- Dwell **＋ Add action** → **Keyboard Shortcut** → **Copy**: returns to List with a new "⌨ Copy" row.
- Add a **Media Control** and, if any packaged apps are listed, a **Launch App**.
- Toggle a row off, then delete a row (confirm the Yes/No modal appears and page dwell targets underneath are inert while it's up).
- Switch to another tab and back → editor is on the List view at page 1 (not a stale picker).
- Summon the Quick Actions grid (hot-edge or toolbar ⚡) → the newly added actions appear as tiles.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat(quick-actions): gaze CRUD editor (view-state picker) in Settings tab"
```

**Checkpoint: feature complete.** Run `dotnet build --no-restore` and `dotnet test --filter "Category!=UI"` — all green.

---

## Final Verification

Run: `dotnet build --no-restore` → zero errors, zero warnings.
Run: `dotnet test --filter "Category!=UI"` → all pass.
Run: `dotnet run --project src/EyeCtrl.App -- --test-mode` → the Task 7 Step 5 manual checklist passes end-to-end.
