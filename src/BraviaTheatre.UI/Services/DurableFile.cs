using System;
using System.IO;

namespace BraviaTheatre.UI.Services;

internal static class DurableFile
{
    /// <summary>
    /// Writes a new file and flushes it to disk, ready to be renamed over the file it replaces.
    /// </summary>
    /// <remarks>
    /// NTFS can commit the rename before the data it points at. Without the flush, a power cut in
    /// between leaves an empty file where the old one was; for credentials that forces a new
    /// Sony sign-in.
    /// </remarks>
    internal static void WriteNew(string path, ReadOnlySpan<byte> contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }
}
