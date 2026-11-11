using System;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The audit log's admin reads: a page newest first, and the export.
/// </summary>
public interface IAccessRolesAudit
{
    /// <summary>
    /// Reads a page of the audit log, newest first.
    /// </summary>
    /// <param name="startIndex">The first line of the page (0 when left out).</param>
    /// <param name="limit">The page's size: 1 to 200, 50 when left out.</param>
    /// <param name="userId">Only lines about this user or by this admin.</param>
    /// <param name="action">Only lines of this action.</param>
    /// <param name="fromUtc">Only lines since then (a time without a zone is UTC).</param>
    /// <param name="toUtc">Only lines before then (a time without a zone is UTC).</param>
    /// <param name="rule">Only lines of this rule, or of a name joined with <c>+</c> that names it.</param>
    /// <param name="before">
    /// Only lines older than the line of this <c>Seq</c>: the next page's cursor, the last line shown, so lines
    /// written meanwhile don't shift the page. The total then counts the lines older than it.
    /// </param>
    /// <returns>The page.</returns>
    QueryResult<AccessRolesAuditEntryDto> Query(int? startIndex, int? limit, Guid? userId, string? action, DateTime? fromUtc, DateTime? toUtc = null, string? rule = null, long? before = null);

    /// <summary>
    /// Exports the audit lines of a time range as they are in the files, oldest first: a JSON Lines download, not cached.
    /// </summary>
    /// <param name="context">The request, for its headers.</param>
    /// <param name="fromUtc">Only lines since then (a time without a zone is UTC).</param>
    /// <param name="toUtc">Only lines before then (a time without a zone is UTC).</param>
    /// <returns>The file.</returns>
    IActionResult Export(HttpContext context, DateTime? fromUtc, DateTime? toUtc);
}
