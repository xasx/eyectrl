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
        try
        {
            return ReadFriendlyName(device);
        }
        finally
        {
            Release(device);
        }
    }

    internal static string? ReadFriendlyName(IMMDevice device)
    {
        var hr = device.OpenPropertyStore(StgmRead, out var store);
        if (hr != SOk || store is null) return null;
        try
        {
            var key = PkeyDeviceFriendlyName;
            hr = store.GetValue(ref key, out var value);
            if (hr != SOk) return null;
            try
            {
                return value.Vt == VtLpwstr ? Marshal.PtrToStringUni(value.PointerValue) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>Our process's session control, or null when winmm has not created it yet (the
    /// first click's PlaySound is what creates it). Never the system sounds session.</summary>
    internal static IAudioSessionControl? FindOwnSessionControl(IAudioSessionEnumerator sessions, out string? sessionGuidText)
    {
        sessionGuidText = null;
        var hr = sessions.GetCount(out var count);
        if (hr != SOk) return null;
        for (var i = 0; i < count; i++)
        {
            hr = sessions.GetSession(i, out var control);
            if (hr != SOk || control is null) continue;
            var control2 = (IAudioSessionControl2)control;
            try
            {
                if (control2.GetProcessId(out var pid) != SOk || pid != Environment.ProcessId) continue;
                if (control2.IsSystemSoundsSession() == SOk) continue;
                if (control2.GetSessionIdentifier(out sessionGuidText) != SOk) sessionGuidText = null;
                return control;
            }
            catch
            {
                Release(control);
            }
        }
        return null;
    }

    /// <summary>The session GUID is the last {...} group of the session identifier.</summary>
    internal static bool TryParseSessionGuid(string? sessionIdentifier, out Guid sessionGuid)
    {
        sessionGuid = Guid.Empty;
        if (string.IsNullOrEmpty(sessionIdentifier)) return false;
        var brace = sessionIdentifier.LastIndexOf('{');
        return brace >= 0 && Guid.TryParse(sessionIdentifier.Substring(brace), out sessionGuid);
    }

    internal static void Release(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // Release races at teardown are harmless here; the diagnostics must never throw.
        }
    }

    // ---- production implementations of the AudioSessionDiagnostics seams ----

    /// <summary>Walks the default endpoint's sessions for OUR process's session and reads its
    /// state. Every failure path returns null/degrades — the caller formats whatever came back.</summary>
    internal sealed class CoreAudioStateReader : AudioSessionDiagnostics.IAudioStateReader
    {
        public AudioSessionDiagnostics.AudioStateSnapshot? Read()
        {
            var enumerator = NewEnumerator();
            try
            {
                var endpointName = ReadDefaultEndpointFriendlyName(enumerator);
                if (enumerator.ActivateSessionManager(out var manager) != SOk || manager is null)
                    return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
                try
                {
                    if (manager.GetSessionEnumerator(out var sessions) != SOk || sessions is null)
                        return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
                    try
                    {
                        var control = FindOwnSessionControl(sessions, out var sessionGuidText);
                        if (control is null)
                            return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, false, null, null, null);
                        try
                        {
                            if (control.GetState(out var state) != SOk)
                                return new AudioSessionDiagnostics.AudioStateSnapshot(endpointName, true, null, null, null);
                            return new AudioSessionDiagnostics.AudioStateSnapshot(
                                endpointName, true, ReadVolume(manager, control, sessionGuidText), ReadMute(manager, control, sessionGuidText), StateName(state));
                        }
                        finally
                        {
                            Release(control);
                        }
                    }
                    finally
                    {
                        Release(sessions);
                    }
                }
                finally
                {
                    Release(manager);
                }
            }
            finally
            {
                Release(enumerator);
            }
        }
    }

    /// <summary>
    /// The session's volume (then mute) for the snapshot line. Review 01 F3: the preferred path
    /// is a straight QI of the FOUND session control for <see cref="ISimpleAudioVolume"/> —
    /// re-resolving by a GUID parsed out of the identifier can open a fresh EMPTY session when
    /// the GUID matches no open session, a side effect a read-only diagnostics walk must not
    /// have. The GUID fallback only runs with a control-derived GUID, which by construction
    /// names the open session. Every failure degrades to a null field (rendered as "?").
    /// </summary>
    private static double? ReadVolume(IAudioSessionManager2 manager, IAudioSessionControl control, string? sessionGuidText)
    {
        try
        {
            var viaQI = (ISimpleAudioVolume)control;
            try
            {
                return viaQI.GetMasterVolume(out var level) == SOk ? level : null;
            }
            finally
            {
                Release(viaQI);
            }
        }
        catch (InvalidCastException)
        {
            // The control does not expose the volume interface: fall back to the manager, with
            // the session's own GUID.
            if (!TryParseSessionGuid(sessionGuidText, out var sessionGuid)) return null;
            var guid = sessionGuid;
            if (manager.GetSimpleAudioVolume(ref guid, 0, out var simpleVolume) != SOk || simpleVolume is null) return null;
            try
            {
                return simpleVolume.GetMasterVolume(out var level) == SOk ? level : null;
            }
            finally
            {
                Release(simpleVolume);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadMute(IAudioSessionManager2 manager, IAudioSessionControl control, string? sessionGuidText)
    {
        try
        {
            var viaQI = (ISimpleAudioVolume)control;
            try
            {
                return viaQI.GetMute(out var mute) == SOk ? mute : null;
            }
            finally
            {
                Release(viaQI);
            }
        }
        catch (InvalidCastException)
        {
            if (!TryParseSessionGuid(sessionGuidText, out var sessionGuid)) return null;
            var guid = sessionGuid;
            if (manager.GetSimpleAudioVolume(ref guid, 0, out var simpleVolume) != SOk || simpleVolume is null) return null;
            try
            {
                return simpleVolume.GetMute(out var mute) == SOk ? mute : null;
            }
            finally
            {
                Release(simpleVolume);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Registers the endpoint notification client immediately and the session events
    /// once our session exists (the retry contract of AudioSessionDiagnostics). Holds every
    /// registration for the app's lifetime; Shutdown unregisters in reverse order.</summary>
    internal sealed class CoreAudioChangeObserver : AudioSessionDiagnostics.IAudioChangeListener
    {
        private readonly object _gate = new();
        private Action<string>? _onChange;
        private IMMDeviceEnumerator? _enumerator;
        private EndpointNotificationClient? _endpointClient;
        private IAudioSessionControl? _sessionControl;
        private SessionEvents? _sessionEvents;
        private bool _endpointAttached, _sessionAttached;

        public bool TryRegister(Action<string> onChangeLine)
        {
            lock (_gate)
            {
                _onChange = onChangeLine;
                if (_endpointAttached) return true;
                var enumerator = NewEnumerator();
                var client = new EndpointNotificationClient(onChangeLine);
                if (enumerator.RegisterEndpointNotificationCallback(client) != SOk)
                {
                    Release(enumerator);
                    return false;
                }
                _enumerator = enumerator;
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
                if (_onChange is null) return false; // TryRegister must come first
                if (_enumerator is null) return false;
                if (_enumerator.ActivateSessionManager(out var manager) != SOk || manager is null) return false;
                try
                {
                    if (manager.GetSessionEnumerator(out var sessions) != SOk || sessions is null) return false;
                    try
                    {
                        var control = FindOwnSessionControl(sessions, out _);
                        if (control is null) return false; // not created yet — retry at the next transition
                        var events = new SessionEvents(_onChange);
                        if (control.RegisterAudioSessionNotification(events) != SOk)
                        {
                            Release(control);
                            return false;
                        }
                        _sessionControl = control;
                        _sessionEvents = events;
                        _sessionAttached = true;
                        return true;
                    }
                    finally
                    {
                        Release(sessions);
                    }
                }
                finally
                {
                    Release(manager);
                }
            }
        }

        public void Shutdown()
        {
            lock (_gate)
            {
                if (_sessionControl is not null && _sessionEvents is not null)
                    _sessionControl.UnregisterAudioSessionNotification(_sessionEvents);
                if (_enumerator is not null && _endpointClient is not null)
                    _enumerator.UnregisterEndpointNotificationCallback(_endpointClient);
                Release(_enumerator);
                Release(_sessionControl);
                _enumerator = null;
                _endpointClient = null;
                _sessionControl = null;
                _sessionEvents = null;
                _endpointAttached = false;
                _sessionAttached = false;
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
                    var enumerator = NewEnumerator();
                    try
                    {
                        if (enumerator.GetDevice(deviceId, out var device) == SOk && device is not null)
                        {
                            try
                            {
                                var name = ReadFriendlyName(device);
                                if (name is not null) return name;
                            }
                            finally
                            {
                                Release(device);
                            }
                        }
                    }
                    finally
                    {
                        Release(enumerator);
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
    /// Non-zero HRESULT or a null manager on any failure (the caller degrades).</summary>
    public static int ActivateSessionManager(this CoreAudioInterop.IMMDeviceEnumerator enumerator,
        out CoreAudioInterop.IAudioSessionManager2? manager)
    {
        manager = null;
        var iid = CoreAudioInterop.IidIAudioSessionManager2;
        var hr = enumerator.GetDefaultAudioEndpoint(CoreAudioInterop.DataFlowRender, CoreAudioInterop.RoleMultimedia, out var device);
        if (hr != CoreAudioInterop.SOk || device is null) return hr == CoreAudioInterop.SOk ? unchecked((int)0x80004005) : hr; // E_FAIL when the endpoint itself is gone
        try
        {
            hr = device.Activate(ref iid, CoreAudioInterop.ClsCtxAll, IntPtr.Zero, out var iface);
            if (hr == CoreAudioInterop.SOk && iface is not null)
            {
                manager = (CoreAudioInterop.IAudioSessionManager2)iface;
                return CoreAudioInterop.SOk;
            }
            return hr == CoreAudioInterop.SOk ? unchecked((int)0x80004005) : hr;
        }
        finally
        {
            CoreAudioInterop.Release(device);
        }
    }
}
