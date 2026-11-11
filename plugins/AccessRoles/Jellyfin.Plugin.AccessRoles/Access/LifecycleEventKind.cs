using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>What happened to a library, as <see cref="LibraryLifecycle"/> found it (the audit log's <c>Library…</c> actions).</summary>
internal enum LifecycleEventKind
{
    /// <summary>A library the catalog didn't know was listed with its item (<c>LibraryAdded</c>).</summary>
    Added,

    /// <summary>A known library got a new name and so a new id; its references were re-pointed (<c>LibraryRenamed</c>).</summary>
    Renamed,

    /// <summary>A known library is no longer listed; it and its references are <c>Orphaned</c> (<c>LibraryRemoved</c>).</summary>
    Removed,

    /// <summary>A library was deleted and created again under the same id: a new library (<c>LibraryRecreated</c>).</summary>
    Recreated
}
