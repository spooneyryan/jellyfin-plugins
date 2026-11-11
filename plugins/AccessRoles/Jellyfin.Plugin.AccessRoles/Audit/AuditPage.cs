using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// A page of the audit log, newest first.
/// </summary>
/// <param name="Items">The lines of the page.</param>
/// <param name="TotalRecordCount">How many lines match the query.</param>
/// <param name="StartIndex">The index of the page's first line among them.</param>
internal sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int TotalRecordCount, int StartIndex);
