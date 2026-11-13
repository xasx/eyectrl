namespace EyeCtrl.App.Tests;

using NFluent;
using Xunit;

// Restored VERBATIM from fe8ff17f (review 04 T1): this class was deleted in the fixup-03 test
// rework while the report claimed the teeth were kept, and mutant N2 (the vtable slot whose
// absence crashes WASAPI mid-typing) survived as a result. Pure metadata assertions — no COM,
// no statics, no queued work — so it intentionally has no collection attribute and runs parallel.

/// <summary>
/// Review 01 F2's pins: every COM interface's IID must equal the Windows SDK header value, and
/// every callback/vtable interface must declare its methods in exact header order. A wrong IID
/// makes the OS answer E_NOINTERFACE (the instrument silently never runs); a wrong order or a
/// dropped slot makes WASAPI call past the CCW's vtable — an access violation mid-typing.
/// Each expected value cites its SDK header and line (Windows Kits 10.0.26100.0).
/// No COM is touched: these are pure metadata assertions.
/// </summary>
public class CoreAudioInteropPinTests
{
    private static void PinIid<T>(string expected, string citation) =>
        Check.WithCustomMessage($"the IID of {typeof(T).Name} drifted from the SDK ({citation})")
            .That(typeof(T).GUID.ToString("D")).IsEqualTo(expected.ToLowerInvariant());

    [Fact]
    public void TheIids_AreTheSdkHeaderValues()
    {
        // mmdeviceapi.h
        PinIid<CoreAudioInterop.IMMDeviceEnumerator>("A95664D2-9614-4F35-A746-DE8DB63617E6", "mmdeviceapi.h:757");
        PinIid<CoreAudioInterop.IMMDevice>("D666063F-1587-4E43-81F1-B948E807363F", "mmdeviceapi.h:427");
        PinIid<CoreAudioInterop.IMMNotificationClient>("7991EEC9-7E89-4D85-8390-6C703CEC60C0", "mmdeviceapi.h:273");
        PinIid<CoreAudioInterop.IPropertyStore>("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99", "propsys.h:516");
        // audiopolicy.h
        PinIid<CoreAudioInterop.IAudioSessionManager2>("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F", "audiopolicy.h:1155");
        PinIid<CoreAudioInterop.IAudioSessionEnumerator>("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8", "audiopolicy.h:1058");
        PinIid<CoreAudioInterop.IAudioSessionControl>("F4B1A599-7266-4319-A8CA-E70ACB11E8CD", "audiopolicy.h:337");
        PinIid<CoreAudioInterop.IAudioSessionControl2>("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d", "audiopolicy.h:545");
        PinIid<CoreAudioInterop.IAudioSessionEvents>("24918ACC-64B3-37C1-8CA9-74A66E9957A8", "audiopolicy.h:151");
        // Audioclient.h
        PinIid<CoreAudioInterop.ISimpleAudioVolume>("87CE5498-68D6-44E5-9215-6DA47EF883D8", "Audioclient.h:1642");
        PinIid<CoreAudioInterop.MMDeviceEnumeratorComObject>("BCDE0395-E52F-467C-8E3D-C4579291692E", "mmdeviceapi.h:1541 (CLSID)");
    }

    private static void PinOrder<T>(string[] expected, string citation) =>
        Check.WithCustomMessage($"the vtable order of {typeof(T).Name} drifted from the SDK ({citation}) — "
            + "a shifted slot makes WASAPI call the wrong managed method or past the vtable entirely")
            .That(typeof(T).GetMethods().Select(m => m.Name).ToArray())
            .ContainsExactly(expected);

    [Fact]
    public void TheCallbackVtables_AreInSdkHeaderOrder()
    {
        // audiopolicy.h:155-190 — the full seven-slot order, OnChannelVolumeChanged and
        // OnGroupingParamChanged included (the two slots whose absence was review 01 F2).
        PinOrder<CoreAudioInterop.IAudioSessionEvents>(
            new[] { "OnDisplayNameChanged", "OnIconPathChanged", "OnSimpleVolumeChanged",
                    "OnChannelVolumeChanged", "OnGroupingParamChanged", "OnStateChanged", "OnSessionDisconnected" },
            "audiopolicy.h:155-190");
        // mmdeviceapi.h:277-299.
        PinOrder<CoreAudioInterop.IMMNotificationClient>(
            new[] { "OnDeviceStateChanged", "OnDeviceAdded", "OnDeviceRemoved", "OnDefaultDeviceChanged", "OnPropertyValueChanged" },
            "mmdeviceapi.h:277-299");
        // audiopolicy.h:337+ (control) + :549-563 (control2 tail).
        PinOrder<CoreAudioInterop.IAudioSessionControl2>(
            new[] { "GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath",
                    "GetGroupingParam", "SetGroupingParam", "RegisterAudioSessionNotification",
                    "UnregisterAudioSessionNotification", "GetSessionIdentifier", "GetSessionInstanceIdentifier",
                    "GetProcessId", "IsSystemSoundsSession", "SetDuckingPreference" },
            "audiopolicy.h:337-563");
        // Audioclient.h:1646-1660.
        PinOrder<CoreAudioInterop.ISimpleAudioVolume>(
            new[] { "SetMasterVolume", "GetMasterVolume", "SetMute", "GetMute" },
            "Audioclient.h:1646-1660");
        // mmdeviceapi.h:431-451.
        PinOrder<CoreAudioInterop.IMMDevice>(
            new[] { "Activate", "OpenPropertyStore", "GetId", "GetState" },
            "mmdeviceapi.h:431-451");
        // mmdeviceapi.h:761-787.
        PinOrder<CoreAudioInterop.IMMDeviceEnumerator>(
            new[] { "EnumAudioEndpoints", "GetDefaultAudioEndpoint", "GetDevice",
                    "RegisterEndpointNotificationCallback", "UnregisterEndpointNotificationCallback" },
            "mmdeviceapi.h:761-787");
        // audiopolicy.h:1062-1065.
        PinOrder<CoreAudioInterop.IAudioSessionEnumerator>(
            new[] { "GetCount", "GetSession" },
            "audiopolicy.h:1062-1065");
    }
}
