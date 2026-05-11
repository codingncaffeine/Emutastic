using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Emutastic.Services.Archives
{
    // Adapter over System.IO.Compression.ZipArchive (built-in BCL). No native
    // dependency, identical behaviour across .NET 8 / 9 / 10.
    internal sealed class ZipRomArchive : IRomArchive
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;

        public ZipRomArchive(string path)
        {
            _stream  = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: false);
        }

        public IEnumerable<IRomArchiveEntry> Entries
        {
            get
            {
                foreach (var e in _archive.Entries)
                    yield return new Entry(e);
            }
        }

        public void Dispose()
        {
            _archive.Dispose();
            // _archive disposes _stream because leaveOpen: false
        }

        private sealed class Entry : IRomArchiveEntry
        {
            private readonly ZipArchiveEntry _e;
            public Entry(ZipArchiveEntry e) { _e = e; }

            public string Key         => _e.FullName;
            public long   Size        => _e.Length;
            // Zip convention: a directory entry has a name ending in '/' and no payload.
            public bool   IsDirectory => _e.FullName.EndsWith("/") || _e.FullName.EndsWith("\\");

            public Stream OpenEntryStream() => _e.Open();

            public void ExtractTo(Stream destination)
            {
                using var src = _e.Open();
                src.CopyTo(destination);
            }
        }
    }
}
