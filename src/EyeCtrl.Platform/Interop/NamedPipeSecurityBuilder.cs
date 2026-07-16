namespace EyeCtrl.Platform.Interop;

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

public sealed class NamedPipeSecurityBuilder
{
    /// <summary>A PipeSecurity granting ReadWrite to the current user only (no NETWORK, no other users).</summary>
    public PipeSecurity BuildCurrentUserOnly()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current user SID.");
        var security = new PipeSecurity();
        // Current user: full read/write. Everyone else (incl. NETWORK): implicitly denied
        // because no allow rule exists for them.
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}

// HeartbeatPipeServer (Task 15) constructs the server with:
//   var server = NamedPipeServerStreamAcl.Create(
//       PipeName, PipeDirection.InOut, MaxAllowedServerInstances, PipeTransmissionMode.Byte,
//       PipeOptions.Asynchronous, 0, 0, new NamedPipeSecurityBuilder().BuildCurrentUserOnly());
