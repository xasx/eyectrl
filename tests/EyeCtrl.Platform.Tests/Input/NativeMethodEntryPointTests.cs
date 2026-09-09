namespace EyeCtrl.Platform.Tests.Input;

using System.Reflection;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;

/// <summary>
/// Gate over the <c>[LibraryImport]</c> declarations in <see cref="NativeMethods"/>.
///
/// <para>
/// Source-generated interop binds lazily: a wrong DLL or a missing A/W <c>EntryPoint</c>
/// compiles green and throws <see cref="EntryPointNotFoundException"/> at the FIRST CALL —
/// which for the escape hatch was on a background thread, i.e. a process kill on every launch
/// (review PR #142: <c>GetCurrentThreadId</c> declared on user32, <c>SetWindowsHookEx</c> and
/// <c>PostThreadMessage</c> without their W entry points). This test resolves every declared
/// import to a real export up front, so that class of bug fails here instead of in the field.
/// </para>
/// </summary>
public partial class NativeMethodEntryPointTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibrary(string fileName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    private static partial IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string name);

    [Fact]
    public void EveryLibraryImport_ResolvesToARealExport()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<LibraryImportAttribute>() is not null)
            .ToList();

        Check.That(methods.Count).IsStrictlyGreaterThan(0);

        var moduleCache = new Dictionary<string, IntPtr>();
        var missing = new List<string>();

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<LibraryImportAttribute>()!;
            // [LibraryImport] defaults to ExactSpelling: no EntryPoint means the bare method
            // name is the export being asked for — which is exactly the trap this test guards.
            var entryPoint = attr.EntryPoint ?? method.Name;

            if (!moduleCache.TryGetValue(attr.LibraryName, out var hModule))
            {
                hModule = LoadLibrary(attr.LibraryName);
                moduleCache[attr.LibraryName] = hModule;
            }

            if (hModule == IntPtr.Zero || GetProcAddress(hModule, entryPoint) == IntPtr.Zero)
                missing.Add($"{attr.LibraryName}!{entryPoint} (declared as {method.Name})");
        }

        Check.WithCustomMessage("declared imports that no DLL exports")
             .That(missing).IsEmpty();
    }

    [Fact]
    public void Msg_MatchesTheNativeX64Layout()
    {
        // Win32 MSG on x64: hwnd(8) message(4+pad) wParam(8) lParam(8) time(4) pt(8, 4-aligned)
        // + tail padding = 48. The original declaration stopped at lParam (32) and every
        // GetMessageW call wrote 16 bytes past it (review PR #142).
        Check.That(Marshal.SizeOf<NativeMethods.MSG>()).IsEqualTo(48);
    }
}
