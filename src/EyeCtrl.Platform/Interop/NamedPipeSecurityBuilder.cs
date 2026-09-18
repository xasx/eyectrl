namespace EyeCtrl.Platform.Interop;

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

public sealed class NamedPipeSecurityBuilder
{
    /// <summary>A PipeSecurity granting FullControl to the current user only (no NETWORK, no other users).</summary>
    public PipeSecurity BuildCurrentUserOnly()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current user SID.");
        var security = new PipeSecurity();
        // Current user: FULL control — ReadWrite alone is not enough. Creating a pipe's SECOND
        // instance (the accept loop after the first client connects, or a reconnect) requires
        // FILE_CREATE_PIPE_INSTANCE on the existing pipe object, which the first instance's own
        // DACL must grant us; with a ReadWrite-only rule the second NamedPipeServerStreamAcl.Create
        // is denied and the accept loop permanently gives up. Everyone else (incl. NETWORK) is
        // implicitly denied because no allow rule exists for them.
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

// HeartbeatPipeServer (Task 15) constructs the server with:
//   var server = NamedPipeServerStreamAcl.Create(
//       PipeName, PipeDirection.InOut, MaxAllowedServerInstances, PipeTransmissionMode.Byte,
//       PipeOptions.Asynchronous, 0, 0, new NamedPipeSecurityBuilder().BuildCurrentUserOnly());
