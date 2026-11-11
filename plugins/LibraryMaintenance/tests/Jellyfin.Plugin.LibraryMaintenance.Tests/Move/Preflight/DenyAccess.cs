using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Preflight;

/// <summary>
/// Denies this account some rights on a folder in a test's temp directory, until disposed (a real ACL, as a share or an
/// administrator would set it).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DenyAccess : IDisposable
{
    private readonly DirectoryInfo _folder;
    private readonly FileSystemAccessRule _rule;

    public DenyAccess(string folder, FileSystemRights rights)
    {
        _folder = new DirectoryInfo(folder);
        _rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        var security = _folder.GetAccessControl();
        security.AddAccessRule(_rule);
        _folder.SetAccessControl(security);
    }

    public void Dispose()
    {
        var security = _folder.GetAccessControl();
        security.RemoveAccessRule(_rule);
        _folder.SetAccessControl(security);
    }
}
