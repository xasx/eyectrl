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
/// Ownership: the reader releases every COM object it obtains; the observer holds its
/// registrations (enumerator, endpoint client, session control, session events) for the app's
/// lifetime and releases them in Shutdown. Reader and observer never share objects.
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
        [PreserveSig] int RegisterDuckNotification(IntPtr systemSoundSessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
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

    internal static IMMDeviceEnumerator AcquireSharedEnumerator()
    {
        var existing = _sharedEnumerator;
        if (existing is not null) return existing;
        lock (EnumeratorLock)
        {
            return _sharedEnumerator ??= NewEnumerator();
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
            if (enumerator.ActivateSessionManager(out var manager) != SOk || manager is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
            if (manager.GetSessionEnumerator(out var sessions) != SOk || sessions is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
            var control = FindOwnSessionControl(sessions);
            if (control is null)
                return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
            if (control.GetState(out var state) != SOk)
                return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, true, null, null, null);
            var (level, mute) = ReadVolumeAndMute(control);
            return new AudioSessionDiagnostics.AudioStateSnapshot(
                endpointName, true, level, mute, StateName(state));
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
        private Action<string>? _onChange;
        private EndpointNotificationClient? _endpointClient;
        private IAudioSessionControl? _sessionControl;
        private SessionEvents? _sessionEvents;
        private bool _endpointAttached, _sessionAttached;
        private volatile bool _shutDown;

        public void SignalShutdown() => _shutDown = true;

        public bool TryRegister(Action<string> onChangeLine)
        {
            lock (_gate)
            {
                _onChange = onChangeLine;
                if (_endpointAttached) return true;
            }
            // COM outside _gate (R3); _comLock serializes concurrent worker registrations.
            // Fixup 03 S1: the lazy acquisition runs HERE, on the worker, inside _comLock —
            // never in the constructor the UI thread touches.
            lock (_comLock)
            {
                if (_shutDown || _endpointAttached) return _endpointAttached;
                _enumerator ??= AcquireSharedEnumerator();
                var client = new EndpointNotificationClient(onChangeLine);
                if (_enumerator.RegisterEndpointNotificationCallback(client) != SOk) return false;
                _endpointClient = client;
                _endpointAttached = true;
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
                _endpointClient = null;
                _sessionControl = null;
                _sessionEvents = null;
                _endpointAttached = false;
                _sessionAttached = false;
            }            finally
            {
                Monitor.Exit(_comLock);
            }
        }
    }

    [ComVisible(true)]
    private sealed class SessionEvents(Action<string> onChange) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string displayName, ref Guid eventContext) => SOk;
        public int OnIconPathChanged(string iconPath, ref Guid eventContext) => SOk;

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            Safe(() => onChange(AudioSessionDiagnostics.FormatVolumeChange(newVolume, newMute)));
            return SOk;
        }

        // Sustain-only slots (audiopolicy.h:172, :181): channel-volume and grouping-param changes
        // are not diagnostics the F5 hypotheses need, but the slots MUST exist — WASAPI calls them
        // by position, and a missing slot sends the call past the CCW's vtable.
        public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumeArray, int changedChannel, ref Guid eventContext) => SOk;

        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => SOk;

        public int OnStateChanged(int newState)
        {
            Safe(() => onChange(AudioSessionDiagnostics.FormatSessionStateChange(StateName(newState))));
            return SOk;
        }

        public int OnSessionDisconnected(int disconnectReason)
        {
            Safe(() => onChange(AudioSessionDiagnostics.FormatSessionDisconnected(DisconnectReasonName(disconnectReason))));
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

    [ComVisible(true)]
    private sealed class EndpointNotificationClient(Action<string> onChange) : IMMNotificationClient
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
                onChange(AudioSessionDiagnostics.FormatDefaultDeviceChanged(FriendlyName(deviceId)));
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
