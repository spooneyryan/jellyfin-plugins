namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// A volume a path is on (<c>VolumeInfoProvider</c>).
/// </summary>
/// <param name="MountPoint">Where it is mounted (<c>C:\</c>, or a folder for a mounted volume).</param>
/// <param name="VolumeId">Its identity, the volume GUID path (<c>\\?\Volume{…}\</c>); two paths are on one volume when these
/// are equal, whatever their drive letters say.</param>
/// <param name="FreeBytes">The bytes the server's account may still write there, or null when unknown.</param>
/// <param name="Label">Its label, or null.</param>
internal sealed record VolumeInfo(string MountPoint, string VolumeId, long? FreeBytes, string? Label);
