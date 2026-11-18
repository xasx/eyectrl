namespace EyeCtrl.App;

using System.Runtime.InteropServices;

/// <summary>
/// Raw Core Audio COM interop for the F5 audio diagnostics (see AudioSessionDiagnostics): the
/// default render endpoint's friendly name, EyeCtrl's own audio session state (volume, mute,
/// state), and change events. The interface declarations are VTABLE-ORDER-EXACT — a wrong
/// order is an in-process crash, not a compile error, so every member is transcribed in the
/// header's order, every method is [PreserveSig] int (HRESULT in, HRESULT out, no marshaled
/// exceptions), and unused slots stay declared with IntPtr parameters rather than deleted.
/// The managed callback classes (SessionEvents, EndpointNotificationClient) are registered as
/// CCWs and are the unproven half on this machine — which is why AudioSessionDiagnostics keeps
/// the show/hide snapshot as a baseline in both modes.
/// <para>
/// Ownership: no eager COM release anywhere (review 02 R1/R2) — the process-wide enumerator and
/// meter, the endpoint registrations and the session registration live for the app's lifetime and
/// are unregistered in Shutdown; every other RCW simply goes out of scope. Native PROPVARIANT
/// memory is still cleared explicitly. The F10 readers (endpoint master volume, meter, ducking)
/// share the same enumerator and follow the same discipline.
/// </para>
/// </summary>
internal static class CoreAudioInterop
{
    internal const int ClsCtxAll = 0x17; // CLSCTX_INPROC_SERVER|HANDLER|LOCAL_SERVER|REMOTE_SERVER
    internal const int StgmRead = 0;
    internal const int DataFlowRender = 0; // eRender
    internal const int RoleMultimedia = 1; // eMultimedia
    internal const int SOk = 0;
    private const short VtLpwstr = 31; // VT_LPWSTR

    internal static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    // SDK: endpointvolume.h:207. Activated on the default render ENDPOINT (IMMDevice.Activate).
    internal static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    // SDK: endpointvolume.h:842. Activated on the default render ENDPOINT.
    internal static readonly Guid IidIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    // PKEY_Device_FriendlyName (functionpkeys.h)
    private static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14,
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public short Vt;
        public short Reserved1;
        public short Reserved2;
        public short Reserved3;
        public IntPtr PointerValue;
        public IntPtr ExtraValue; // the PROPVARIANT union is 16 bytes (CLSID member) — keep the size honest
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>The MMDeviceEnumerator coclass, activated by plain <c>new</c> (the NAudio-proven
    /// pattern on modern .NET): a ComImport coclass cannot be cast to its interface directly any
    /// more (CS0030), so the cast hops through object — at runtime that is the RCW's QI.</summary>
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal sealed class MMDeviceEnumeratorComObject
    {
    }

    internal static IMMDeviceEnumerator NewEnumerator() =>
        (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();

    // ---- COM interfaces (audiosessiontypes.h / mmdeviceapi.h), vtable order exact ----

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        // SDK: mmdeviceapi.h:757 (IID), vtable mmdeviceapi.h:761-787.
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        // SDK: mmdeviceapi.h:427 (IID), vtable mmdeviceapi.h:431-451.
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        // SDK: propsys.h:516 (IID). Vtable: GetCount, GetAt, GetValue, SetValue, Commit.
        [PreserveSig] int GetCount(out int propertyCount);
        [PreserveSig] int GetAt(int property, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // SDK: audiopolicy.h:1155 (IID); vtable order audiopolicy.h:1202-1244 (IAudioSessionManager
        // part first: GetAudioSessionControl, GetSimpleAudioVolume — see audiopolicy.h:780, :824).
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out ISimpleAudioVolume audioVolume);
        // IAudioSessionManager2 (unused slots stay declared — the vtable must line up)
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        // audiopolicy.h:1168-1175. A NULL sessionID asks for ALL ducking notifications rather
        // than only our own session's (the SDK's documented "do not alter our stream" mode) —
        // which is also why this can register before winmm has created our session.
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IAudioVolumeDuckNotification duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IAudioVolumeDuckNotification duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        // SDK: audiopolicy.h:1058 (IID), vtable audiopolicy.h:1062-1065.
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        // SDK: audiopolicy.h:337 (IID). Vtable: state, display/icon path, grouping param, events.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // SDK: audiopolicy.h:545 (IID). IAudioSessionControl vtable first, then the Control2 tail
        // (audiopolicy.h:549-563).
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out int processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        // SDK: Audioclient.h:1642 (IID), vtable Audioclient.h:1646-1660.
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>The render endpoint's master volume and mute (F10). SDK: endpointvolume.h:207
    /// (IID), vtable endpointvolume.h:210-300 — all EIGHTEEN slots in header order. The Set*
    /// slots are declared only to keep the vtable exact; this app never writes a volume or a
    /// mute (the owner controls them).</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int GetChannelCount(out int channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out int step, out int stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    /// <summary>The payload prefix of AUDIO_VOLUME_NOTIFICATION_DATA (endpointvolume.h:93): the
    /// native struct ends in a variable-length channel array this app never reads. Field order
    /// and sizes are the header's: GUID (16), BOOL (4), float (4), UINT (4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        [MarshalAs(UnmanagedType.Bool)] public bool Muted;
        public float MasterVolume;
        public int ChannelCount;
    }

    /// <summary>Implemented by the managed <see cref="EndpointVolumeCallback"/> CCW, registered on
    /// the render endpoint. SDK: endpointvolume.h:123 (IID), vtable endpointvolume.h:127 — one
    /// slot, OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA). Declared as IntPtr so the variable-length
    /// channel tail never has to be marshaled; the callback reads only the fixed prefix.</summary>
    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F95"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotify);
    }

    /// <summary>The render endpoint's peak meter (F10's "did the click reach the speakers").
    /// SDK: endpointvolume.h:842 (IID), vtable endpointvolume.h:846-860. Activated on the
    /// endpoint; per-session metering is not exposed for a session another process's audio client
    /// owns (winmm owns EyeCtrl's), so the endpoint meter is the read-only instrument available.</summary>
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int channelCount);
        [PreserveSig] int GetChannelsPeakValues(int channelCount, IntPtr peakValues);
        [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
    }

    /// <summary>Implemented by the managed <see cref="DuckNotification"/> CCW, registered on the
    /// session manager. SDK: audiopolicy.h:877 (IID), vtable audiopolicy.h:881-886 — two slots.</summary>
    [ComImport, Guid("C3B284D4-6D39-4359-B3CF-B56DDB3BB39C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioVolumeDuckNotification
    {
        [PreserveSig] int OnVolumeDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionID, int countCommunicationSessions);
        [PreserveSig] int OnVolumeUnduckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionID);
    }

    /// <summary>Implemented by the managed <see cref="SessionEvents"/> CCW, registered on our
    /// session control. Fires on WASAPI threads — the handlers only format and log.
    /// SDK: audiopolicy.h:151 (IID), vtable audiopolicy.h:155-190 — ALL SEVEN slots, in header
    /// order; dropping a slot shifts every later call on a WASAPI thread (an access violation,
    /// review 01 F2).</summary>
    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float newVolume, [MarshalAs(UnmanagedType.Bool)] bool newMute, ref Guid eventContext);
        [PreserveSig] int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumeArray, int changedChannel, ref Guid eventContext);
        [PreserveSig] int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext);
        [PreserveSig] int OnStateChanged(int newState);
        [PreserveSig] int OnSessionDisconnected(int disconnectReason);
    }

    /// <summary>Implemented by the managed <see cref="EndpointNotificationClient"/> CCW,
    /// registered on the device enumerator. SDK: mmdeviceapi.h:273 (IID), vtable :277-299.</summary>
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey propertyKey);
    }

    // ---- names for the closed int vocabularies (never a raw number in a log line) ----

    internal static string StateName(int audioSessionState) => audioSessionState switch
    {
        0 => "Inactive",
        1 => "Active",
        2 => "Expired",
        _ => $"Unknown({audioSessionState})",
    };

    internal static string DisconnectReasonName(int reason) => reason switch
    {
        0 => "DeviceDisconnected",
        1 => "ServiceShutdown",
        2 => "FormatChanged",
        3 => "SessionDisconnected",
        4 => "RemoteSessionChanged",
        5 => "ServerShutdown",
        _ => $"Unknown({reason})",
    };

    // ---- guarded helpers used by both the reader and the observer ----

    internal static string? ReadDefaultEndpointFriendlyName(IMMDeviceEnumerator enumerator)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device);
        if (hr != SOk || device is null) return null;
        // Release discipline (review 02 R1/R2): no eager release — the RCW simply goes out of scope.
        return ReadFriendlyName(device);
    }

    internal static string? ReadFriendlyName(IMMDevice device)
    {
        var hr = device.OpenPropertyStore(StgmRead, out var store);
        if (hr != SOk || store is null) return null;
        var key = PkeyDeviceFriendlyName;
        hr = store.GetValue(ref key, out var value);
        if (hr != SOk) return null;
        try
        {
            // PropVariantClear frees NATIVE memory, not an RCW — still required.
            return value.Vt == VtLpwstr ? Marshal.PtrToStringUni(value.PointerValue) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>The render endpoint's master volume scalar (0..1) and mute flag (F10). Read-only:
    /// the app never calls SetMasterVolumeLevel*/SetMute. QIs the endpoint object for
    /// IAudioEndpointVolume (endpointvolume.h:207); any failure degrades to nulls, rendered "?".
    /// The device RCW simply goes out of scope (the release discipline above).</summary>
    internal static (double? Volume, bool? Mute) ReadEndpointVolumeAndMute(IMMDeviceEnumerator enumerator)
    {
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device);
            if (hr != SOk || device is null) return (null, null);
            var iid = IidIAudioEndpointVolume;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var iface) != SOk || iface is null)
                return (null, null);
            var volume = (IAudioEndpointVolume)iface;
            if (volume.GetMasterVolumeLevelScalar(out var level) != SOk || volume.GetMute(out var mute) != SOk)
                return (null, null);
            return (level, mute);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Our process's session control, or null when winmm has not created it yet (the
    /// first click's PlaySound is what creates it). Never the system sounds session. The returned
    /// control is NOT released by this walk — the diagnostics path lets RCWs go (see the release
    /// discipline on <see cref="AcquireSharedEnumerator"/>).</summary>
    internal static IAudioSessionControl? FindOwnSessionControl(IAudioSessionEnumerator sessions)
    {
        var hr = sessions.GetCount(out var count);
        if (hr != SOk) return null;
        for (var i = 0; i < count; i++)
        {
            hr = sessions.GetSession(i, out var control);
            if (hr != SOk || control is null) continue;
            try
            {
                var control2 = (IAudioSessionControl2)control;
                if (control2.GetProcessId(out var pid) != SOk || pid != Environment.ProcessId) continue;
                if (control2.IsSystemSoundsSession() == SOk) continue;
                return control;
            }
            catch
            {
                // A session that refuses the Control2 QI is not ours to read; move on.
            }
        }
        return null;
    }

    /// <summary>
    /// ONE process-wide enumerator RCW, shared by the reader, the observer and the default-device
    /// callback, and deliberately NEVER released (review 02 R2): MMDeviceEnumerator hands out one
    /// identity per MTA, so every activation here returns the same RCW — a FinalReleaseComObject
    /// by any holder separates it for all the others (that was the silent re-registration kill),
    /// and even a balanced ReleaseComObject by one holder unbalances another's in-flight COM call.
    /// A diagnostics path does not need eager release; the process exit cleans up.
    /// <para>
    /// The CoCreate costs ~44 ms cold (review 03 S1), so this must only be called from the
    /// diagnostics worker — the observer acquires lazily inside its _comLock (pinned per INSTANCE
    /// via HasEnumerator), and construction of the reader/observer never touches it. The process
    /// exit releases it.
    /// </para>
    /// </summary>
    private static readonly object EnumeratorLock = new();
    private static IMMDeviceEnumerator? _sharedEnumerator;

    /// <summary>
    /// Review 05 V3 test seam: the factory behind <see cref="AcquireSharedEnumerator"/>, so a
    /// worker registration's ACQUISITION can be pinned with a managed fake and no real COM. The
    /// production default stays <see cref="NewEnumerator"/> (the real MMDeviceEnumerator coclass),
    /// held by the composition/source pin in the tests.
    /// </summary>
    internal static Func<IMMDeviceEnumerator>? EnumeratorFactoryForTesting { get; set; }

    /// <summary>Test-only reset so a fake-factory test gets a fresh shared instance.</summary>
    internal static void ResetSharedEnumeratorForTesting() => _sharedEnumerator = null;

    internal static IMMDeviceEnumerator AcquireSharedEnumerator()
    {
        var existing = _sharedEnumerator;
        if (existing is not null) return existing;
        lock (EnumeratorLock)
        {
            return _sharedEnumerator ??= EnumeratorFactoryForTesting?.Invoke() ?? NewEnumerator();
        }
    }

    /// <summary>
    /// ONE process-wide endpoint meter RCW (F10), acquired lazily on the diagnostics worker (the
    /// IMMDevice activation is COM) and never released — the same discipline as the shared
    /// enumerator. Invalidated on a default-device change so the next sample follows the new
    /// endpoint: the meter measures ONE device and would otherwise keep reading the old one.
    /// </summary>
    private static IAudioMeterInformation? _sharedMeter;

    internal static IAudioMeterInformation? AcquireSharedMeter()
    {
        var existing = _sharedMeter;
        if (existing is not null) return existing;
        lock (EnumeratorLock)
        {
            if (_sharedMeter is not null) return _sharedMeter;
            var iid = IidIAudioMeterInformation;
            var hr = AcquireSharedEnumerator()
                .GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device);
            if (hr != SOk || device is null) return null;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var iface) != SOk || iface is null)
                return null;
            return _sharedMeter = (IAudioMeterInformation)iface;
        }
    }

    /// <summary>Called from the default-device callback (a WASAPI thread): the cached meter
    /// belongs to the old endpoint. The next sampler acquires the new one on the worker.</summary>
    internal static void InvalidateSharedMeter() => _sharedMeter = null;

    /// <summary>
    /// F10's production meter probe: poll the endpoint peak while a click plays. One meter
    /// reading covers the previous device period (endpointvolume.h GetPeakValue remarks), so a
    /// ~20 ms click stays visible across roughly two readings; a 2 ms cadence plus a 1.5 s cap
    /// also catches a late (device-wake) start, which is the add-on's 0.4 s case. Sleeps only on
    /// the diagnostics worker — never on a key or the UI thread.
    /// </summary>
    internal sealed class CoreAudioMeterProbe : AudioSessionDiagnostics.IAudioMeterProbe
    {
        internal const int WindowMs = 1500;
        internal const int IntervalMs = 2;
        private const int InitialDelayMs = 2;

        /// <summary>null = the meter could not be read at all (unconfigured endpoint, COM
        /// failure); 0 = read fine and the endpoint stayed silent. Never throws.</summary>
        public float? SamplePeak(Func<bool> stop)
        {
            try
            {
                var meter = AcquireSharedMeter();
                if (meter is null) return null;
                var anyRead = false;
                float max = 0;
                var deadline = Environment.TickCount64 + WindowMs;
                Thread.Sleep(InitialDelayMs); // winmm starts the sound asynchronously
                while (true)
                {
                    if (meter.GetPeakValue(out var peak) == SOk)
                    {
                        anyRead = true;
                        if (peak > max) max = peak;
                        // Reached: stop polling. The threshold is the same one the counter uses,
                        // so a noise floor far below it cannot end the window before the sound.
                        if (peak > AudioSessionDiagnostics.MeterReachedThreshold) return max;
                    }
                    if (Environment.TickCount64 >= deadline || stop()) break;
                    Thread.Sleep(IntervalMs);
                }
                return anyRead ? max : null;
            }
            catch
            {
                return null;
            }
        }
    }

    // ---- production implementations of the AudioSessionDiagnostics seams ----

    /// <summary>Walks the default endpoint's sessions for OUR process's session and reads its
    /// state. Every failure path returns null/degrades — the caller formats whatever came back.
    /// Release discipline (review 02 R1/R2): no eager COM release anywhere on this path — the
    /// enumerator is the process-wide shared one, and every other RCW simply goes out of scope.</summary>
    internal sealed class CoreAudioStateReader : AudioSessionDiagnostics.IAudioStateReader
    {
        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            var enumerator = AcquireSharedEnumerator();
            var endpointName = ReadDefaultEndpointFriendlyName(enumerator);
            // F10: the endpoint's own master volume/mute, read on the same default device the
            // clicks go to. Kept on every snapshot (boundaries included) so the owner can see
            // whether Windows' answer changed between two shows.
            var (endpointVolume, endpointMute) = ReadEndpointVolumeAndMute(enumerator);
            if (enumerator.ActivateSessionManager(out var manager) != SOk || manager is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(
                    endpointName, endpointVolume, endpointMute, false, null, null, null);
            if (manager.GetSessionEnumerator(out var sessions) != SOk || sessions is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(
                    endpointName, endpointVolume, endpointMute, false, null, null, null);
            var control = FindOwnSessionControl(sessions);
            if (control is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(
                    endpointName, endpointVolume, endpointMute, false, null, null, null);
            if (control.GetState(out var state) != SOk)
                return new AudioSessionDiagnostics.AudioStateSnapshot(
                    endpointName, endpointVolume, endpointMute, true, null, null, null);
            var (level, mute) = ReadVolumeAndMute(control);
            return new AudioSessionDiagnostics.AudioStateSnapshot(
                endpointName, endpointVolume, endpointMute, true, level, mute, StateName(state));
        }
    }

    /// <summary>
    /// The session's volume AND mute, read from ONE cast of the found control (review 02 R1:
    /// casting an RCW returns the same RCW, so releasing the cast separates the control itself —
    /// the previous two-method read lost every mute value that way). A control that does not
    /// expose the volume interface (or any failure) degrades to nulls, rendered as "?".
    /// </summary>
    private static (double? Level, bool? Mute) ReadVolumeAndMute(IAudioSessionControl control)
    {
        try
        {
            var volume = (ISimpleAudioVolume)control;
            if (volume.GetMasterVolume(out var level) == SOk && volume.GetMute(out var mute) == SOk)
                return ((double)level, mute);
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }


    /// <summary>Registers the endpoint notification client immediately and the session events
    /// once our session exists (the retry contract of AudioSessionDiagnostics). Holds every
    /// registration for the app's lifetime; Shutdown unregisters in reverse order.</summary>
    internal sealed class CoreAudioChangeObserver : AudioSessionDiagnostics.IAudioChangeListener
    {
        private readonly object _gate = new();
        // R3 (review 02): _gate is state-only and _comLock is taken ONLY by the diagnostics worker
        // (pool threads) — never by the UI thread — so a hung AudioSrv RPC can delay the queued
        // work but can never freeze EnsureProductionWiring, Shutdown or a key.
        // Fixup 03 S1: the enumerator is LAZY — its CoCreate costs ~44 ms cold and must run on the
        // worker (inside _comLock), never in this constructor (which the UI thread's
        // EnsureProductionWiring touches). Shutdown uses it only if a registration ever happened.
        // Review 04 T3: HasEnumerator exposes the laziness per INSTANCE — construction and wiring
        // must leave it null; only a worker registration fills it.
        private readonly object _comLock = new();
        private IMMDeviceEnumerator? _enumerator;
        internal bool HasEnumerator => _enumerator is not null;
        private Action<AudioSessionDiagnostics.AudioChange>? _onChange;
        private EndpointNotificationClient? _endpointClient;
        private IAudioSessionControl? _sessionControl;
        private SessionEvents? _sessionEvents;
        // F10's two endpoint-level instruments: the endpoint master volume/mute callback and the
        // ducking notification. Both live as long as the observer (app lifetime) and are
        // unregistered in Shutdown.
        private IAudioEndpointVolume? _endpointVolume;
        private EndpointVolumeCallback? _endpointVolumeCallback;
        private IAudioSessionManager2? _duckManager;
        private DuckNotification? _duck;
        private bool _endpointAttached, _endpointVolumeAttached, _duckAttached, _sessionAttached;
        private volatile bool _shutDown;

        public void SignalShutdown() => _shutDown = true;

        /// <summary>Attach every ENDPOINT-level instrument: default-device changes (F5), the
        /// endpoint master volume/mute callback (F10) and ducking notifications (F10). Returns
        /// true only when ALL THREE are attached, so a transient failure is retried at the next
        /// transition instead of leaving one instrument silently blind. F10 fixup: the session
        /// events are no longer gated on this result — a duck registration failure must not cost
        /// the session instrument.</summary>
        public bool TryRegister(Action<AudioSessionDiagnostics.AudioChange> onChangeLine)
        {
            lock (_gate)
            {
                _onChange = onChangeLine;
                if (_endpointAttached && _endpointVolumeAttached && _duckAttached) return true;
            }
            // COM outside _gate (R3); _comLock serializes concurrent worker registrations.
            // Fixup 03 S1: the lazy acquisition runs HERE, on the worker, inside _comLock —
            // never in the constructor the UI thread touches.
            lock (_comLock)
            {
                if (_shutDown) return false;
                _enumerator ??= AcquireSharedEnumerator();
                if (!_endpointAttached)
                {
                    var client = new EndpointNotificationClient(onChangeLine);
                    if (_enumerator.RegisterEndpointNotificationCallback(client) != SOk) return false;
                    _endpointClient = client;
                    _endpointAttached = true;
                }
                if (!_endpointVolumeAttached)
                {
                    if (_enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != SOk
                        || device is null) return false;
                    var iid = IidIAudioEndpointVolume;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var iface) != SOk || iface is null)
                        return false;
                    var volume = (IAudioEndpointVolume)iface;
                    var volumeCallback = new EndpointVolumeCallback(onChangeLine);
                    if (volume.RegisterControlChangeNotify(volumeCallback) != SOk) return false;
                    _endpointVolume = volume;
                    _endpointVolumeCallback = volumeCallback;
                    _endpointVolumeAttached = true;
                }
                if (!_duckAttached)
                {
                    // NULL session id = receive ALL ducking notifications (RegisterDuckNotification
                    // docs); our own session does not exist yet at the first click.
                    if (_enumerator.ActivateSessionManager(out var manager) != SOk || manager is null) return false;
                    var duck = new DuckNotification(onChangeLine);
                    if (manager.RegisterDuckNotification(null, duck) != SOk) return false;
                    _duckManager = manager;
                    _duck = duck;
                    _duckAttached = true;
                }
                return true;
            }
        }

        public bool TryRegisterSessionEvents()
        {
            lock (_gate)
            {
                if (_sessionAttached) return true;
            }
            if (_shutDown) return false;
            if (_onChange is null) return false; // TryRegister must come first
            // COM outside _gate (R3).
            lock (_comLock)
            {
                if (_shutDown || _sessionAttached) return _sessionAttached;
                _enumerator ??= AcquireSharedEnumerator(); // fixup 03 S1: lazy, worker-only
                if (_enumerator.ActivateSessionManager(out var manager) != SOk || manager is null) return false;
                if (manager.GetSessionEnumerator(out var sessions) != SOk || sessions is null) return false;
                var control = FindOwnSessionControl(sessions);
                if (control is null) return false; // not created yet — retry at the next transition
                var events = new SessionEvents(_onChange);
                if (control.RegisterAudioSessionNotification(events) != SOk) return false;
                _sessionControl = control;
                _sessionEvents = events;
                _sessionAttached = true;
                return true;
            }
        }

        public void Shutdown()
        {
            _shutDown = true; // no late registration after shutdown (review 02 R4)
            // Bounded: never wait on an in-flight COM call from the UI thread (R3). If the worker
            // holds _comLock past 200 ms, the process exit cleans up instead.
            if (!Monitor.TryEnter(_comLock, 200)) return;
            try
            {
                if (_sessionControl is not null && _sessionEvents is not null)
                    _sessionControl.UnregisterAudioSessionNotification(_sessionEvents);
                if (_enumerator is not null && _endpointClient is not null)
                    _enumerator.UnregisterEndpointNotificationCallback(_endpointClient);
                if (_endpointVolume is not null && _endpointVolumeCallback is not null)
                    _endpointVolume.UnregisterControlChangeNotify(_endpointVolumeCallback);
                if (_duckManager is not null && _duck is not null)
                    _duckManager.UnregisterDuckNotification(_duck);
                _endpointClient = null;
                _sessionControl = null;
                _sessionEvents = null;
                _endpointVolume = null;
                _endpointVolumeCallback = null;
                _duckManager = null;
                _duck = null;
                _endpointAttached = false;
                _endpointVolumeAttached = false;
                _duckAttached = false;
                _sessionAttached = false;
            }            finally
            {
                Monitor.Exit(_comLock);
            }
        }
    }

    [ComVisible(true)]
    private sealed class SessionEvents(Action<AudioSessionDiagnostics.AudioChange> onChange) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string displayName, ref Guid eventContext) => SOk;
        public int OnIconPathChanged(string iconPath, ref Guid eventContext) => SOk;

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            Safe(() => onChange(new AudioSessionDiagnostics.AudioChange(
                AudioSessionDiagnostics.AudioChangeKind.Volume,
                AudioSessionDiagnostics.FormatVolumeChange(newVolume, newMute))));
            return SOk;
        }

        // Sustain-only slots (audiopolicy.h:172, :181): channel-volume and grouping-param changes
        // are not diagnostics the F5 hypotheses need, but the slots MUST exist — WASAPI calls them
        // by position, and a missing slot sends the call past the CCW's vtable.
        public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumeArray, int changedChannel, ref Guid eventContext) => SOk;

        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => SOk;

        public int OnStateChanged(int newState)
        {
            // The state name is carried structurally too: the F10 late-start metric measures the
            // click→Active delay from this callback, not by parsing a log line.
            var stateName = StateName(newState);
            Safe(() => onChange(new AudioSessionDiagnostics.AudioChange(
                AudioSessionDiagnostics.AudioChangeKind.SessionState,
                AudioSessionDiagnostics.FormatSessionStateChange(stateName),
                stateName)));
            return SOk;
        }

        public int OnSessionDisconnected(int disconnectReason)
        {
            Safe(() => onChange(new AudioSessionDiagnostics.AudioChange(
                AudioSessionDiagnostics.AudioChangeKind.SessionDisconnected,
                AudioSessionDiagnostics.FormatSessionDisconnected(DisconnectReasonName(disconnectReason)))));
            return SOk;
        }

        private static void Safe(Action emit)
        {
            try
            {
                emit();
            }
            catch
            {
                // Never out of a COM callback: WASAPI ignores the HRESULT but the line is lost —
                // the show/hide snapshots remain the baseline either way.
            }
        }
    }

    /// <summary>
    /// The endpoint master volume/mute callback (F10). Registered on the render endpoint. Logs
    /// ONE line whenever either value changes; duplicate notifications for the same state (the
    /// endpoint sends one per channel change too) are swallowed so the log stays a change log,
    /// not a per-key one. The lock is private to this callback and never taken on the key path.
    /// </summary>
    [ComVisible(true)]
    internal sealed class EndpointVolumeCallback(Action<AudioSessionDiagnostics.AudioChange> onChange) : IAudioEndpointVolumeCallback
    {
        private readonly object _gate = new();
        private bool _hasLast;
        private float _lastVolume;
        private bool _lastMute;

        public int OnNotify(IntPtr pNotify)
        {
            try
            {
                if (pNotify == IntPtr.Zero) return SOk;
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotify);
                lock (_gate)
                {
                    if (_hasLast && data.MasterVolume == _lastVolume && data.Muted == _lastMute) return SOk;
                    _hasLast = true;
                    _lastVolume = data.MasterVolume;
                    _lastMute = data.Muted;
                }
                onChange(new AudioSessionDiagnostics.AudioChange(
                    AudioSessionDiagnostics.AudioChangeKind.EndpointVolume,
                    AudioSessionDiagnostics.FormatEndpointVolumeChange(data.MasterVolume, data.Muted)));
            }
            catch
            {
                // Never out of a COM callback.
            }
            return SOk;
        }
    }

    /// <summary>
    /// The ducking notification callback (F10). Registered with a NULL session id, so Windows
    /// sends EVERY communication ducking event (the documented "do not alter our stream" mode).
    /// The line is emitted on the WASAPI callback thread; Serilog's own timestamp is the event
    /// time the owner needs.
    /// </summary>
    [ComVisible(true)]
    internal sealed class DuckNotification(Action<AudioSessionDiagnostics.AudioChange> onChange) : IAudioVolumeDuckNotification
    {
        public int OnVolumeDuckNotification(string? sessionID, int countCommunicationSessions)
        {
            try
            {
                onChange(new AudioSessionDiagnostics.AudioChange(
                    AudioSessionDiagnostics.AudioChangeKind.DuckStarted,
                    AudioSessionDiagnostics.FormatDuckStart(countCommunicationSessions)));
            }
            catch
            {
                // Never out of a COM callback.
            }
            return SOk;
        }

        public int OnVolumeUnduckNotification(string? sessionID)
        {
            try
            {
                onChange(new AudioSessionDiagnostics.AudioChange(
                    AudioSessionDiagnostics.AudioChangeKind.DuckEnded,
                    AudioSessionDiagnostics.FormatDuckEnd()));
            }
            catch
            {
                // Never out of a COM callback.
            }
            return SOk;
        }
    }

    [ComVisible(true)]
    private sealed class EndpointNotificationClient(Action<AudioSessionDiagnostics.AudioChange> onChange) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => SOk;
        public int OnDeviceAdded(string deviceId) => SOk;
        public int OnDeviceRemoved(string deviceId) => SOk;
        public int OnPropertyValueChanged(string deviceId, PropertyKey propertyKey) => SOk;

        public int OnDefaultDeviceChanged(int dataFlow, int role, string deviceId)
        {
            if (dataFlow != DataFlowRender) return SOk;
            try
            {
                // F10: the meter belongs to the old endpoint; the next sampler acquires the new
                // one on the worker (never on this WASAPI callback thread).
                InvalidateSharedMeter();
                onChange(new AudioSessionDiagnostics.AudioChange(
                    AudioSessionDiagnostics.AudioChangeKind.DefaultDevice,
                    AudioSessionDiagnostics.FormatDefaultDeviceChanged(FriendlyName(deviceId))));
            }
            catch
            {
                // Never out of a COM callback.
            }
            return SOk;
        }

        private static string FriendlyName(string? deviceId)
        {
            try
            {
                if (deviceId is not null)
                {
                    // The process-wide shared enumerator (review 02 R2): creating and releasing a
                    // second one here separated the observer's held RCW on every device change.
                    var enumerator = AcquireSharedEnumerator();
                    if (enumerator.GetDevice(deviceId, out var device) == SOk && device is not null)
                    {
                        var name = ReadFriendlyName(device);
                        if (name is not null) return name;
                    }
                }
            }
            catch
            {
                // fall through to the device id
            }
            return deviceId ?? "<unknown>";
        }
    }
}

/// <summary>One static extension point so both production classes share the activation walk
/// without exposing the raw COM object outside this pair of files.</summary>
internal static class CoreAudioEnumeratorExtensions
{
    /// <summary>Activates IAudioSessionManager2 on the enumerator's default render endpoint.
    /// Non-zero HRESULT or a null manager on any failure (the caller degrades). Release
    /// discipline: the device RCW simply goes out of scope (review 02 R1/R2).</summary>
    public static int ActivateSessionManager(this CoreAudioInterop.IMMDeviceEnumerator enumerator,
        out CoreAudioInterop.IAudioSessionManager2? manager)
    {
        manager = null;
        var iid = CoreAudioInterop.IidIAudioSessionManager2;
        var hr = enumerator.GetDefaultAudioEndpoint(CoreAudioInterop.DataFlowRender, CoreAudioInterop.RoleMultimedia, out var device);
        if (hr != CoreAudioInterop.SOk || device is null) return hr == CoreAudioInterop.SOk ? unchecked((int)0x80004005) : hr; // E_FAIL when the endpoint itself is gone
        hr = device.Activate(ref iid, CoreAudioInterop.ClsCtxAll, IntPtr.Zero, out var iface);
        if (hr == CoreAudioInterop.SOk && iface is not null)
        {
            manager = (CoreAudioInterop.IAudioSessionManager2)iface;
            return CoreAudioInterop.SOk;
        }
        return hr == CoreAudioInterop.SOk ? unchecked((int)0x80004005) : hr;
    }
}
