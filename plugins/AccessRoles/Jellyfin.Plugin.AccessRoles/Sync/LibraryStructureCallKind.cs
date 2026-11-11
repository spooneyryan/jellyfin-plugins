namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>Which library structure call the filter saw.</summary>
internal enum LibraryStructureCallKind
{
    /// <summary><c>AddVirtualFolder</c>.</summary>
    Add,

    /// <summary><c>RemoveVirtualFolder</c>.</summary>
    Remove,

    /// <summary><c>RenameVirtualFolder</c>.</summary>
    Rename,

    /// <summary>The media-path actions and <c>UpdateLibraryOptions</c>: folders or the Enabled switch changed.</summary>
    Update
}
