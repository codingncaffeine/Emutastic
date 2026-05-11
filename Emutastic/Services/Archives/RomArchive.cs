using System;
using System.Collections.Generic;
using System.IO;

namespace Emutastic.Services.Archives
{
    // Thin internal facade over the two libraries we use for ROM/BIOS archive
    // handling: System.IO.Compression for .zip (rock-solid, .NET BCL) and
    // SevenZipExtractor for .7z and .rar (stable native wrapper around 7z.dll
    // bundled inside the single-file .exe). Lets the rest of the codebase open
    // any supported archive through one API without a SharpCompress dependency.

    public interface IRomArchive : IDisposable
    {
        IEnumerable<IRomArchiveEntry> Entries { get; }
    }

    public interface IRomArchiveEntry
    {
        /// <summary>Path of the entry inside the archive (uses forward slashes for zip).</summary>
        string Key { get; }

        /// <summary>Uncompressed size in bytes. May be -1 if unknown.</summary>
        long Size { get; }

        bool IsDirectory { get; }

        /// <summary>
        /// Returns a stream of the entry's bytes. For zip this is the underlying
        /// deflate stream. For 7z/rar the entry is extracted to a MemoryStream
        /// first — fine for small files (BIOS, manifests) but loads the whole
        /// entry into memory. Use <see cref="ExtractTo"/> for large entries
        /// (ROM ISOs, etc.) to stream directly to disk.
        /// </summary>
        Stream OpenEntryStream();

        /// <summary>Streams the entry's bytes directly into <paramref name="destination"/>.</summary>
        void ExtractTo(Stream destination);
    }

    public static class RomArchive
    {
        /// <summary>
        /// Opens an archive by file path. Routes by extension:
        /// .zip → System.IO.Compression.ZipArchive
        /// .7z / .rar / .iso / .tar / .gz / others → SevenZipExtractor (7z.dll)
        /// </summary>
        public static IRomArchive Open(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Archive path is empty.", nameof(path));

            string ext = Path.GetExtension(path);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return new ZipRomArchive(path);

            return new SevenZipRomArchive(path);
        }
    }
}
