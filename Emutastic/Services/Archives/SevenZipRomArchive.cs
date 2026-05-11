using System.Collections.Generic;
using System.IO;
using SevenZipExtractor;

namespace Emutastic.Services.Archives
{
    // Adapter over SevenZipExtractor (adoshin) — handles .7z, .rar, .iso, .tar,
    // .gz and a long tail of other formats via the bundled 7z.dll. The native
    // DLL ships inside the single-file .exe via the nupkg's runtimes/win-x64/native/
    // payload and IncludeNativeLibrariesForSelfExtract — never sits next to the
    // .exe, never needs to travel with PortableData.
    internal sealed class SevenZipRomArchive : IRomArchive
    {
        private readonly ArchiveFile _archive;

        public SevenZipRomArchive(string path)
        {
            _archive = new ArchiveFile(path);
        }

        public IEnumerable<IRomArchiveEntry> Entries
        {
            get
            {
                foreach (var e in _archive.Entries)
                    yield return new Entry(e);
            }
        }

        public void Dispose() => _archive.Dispose();

        private sealed class Entry : IRomArchiveEntry
        {
            private readonly SevenZipExtractor.Entry _e;
            public Entry(SevenZipExtractor.Entry e) { _e = e; }

            public string Key         => _e.FileName;
            // SevenZipExtractor reports Size as ulong; cast to long. Real-world
            // archives never exceed long.MaxValue, but a -1 sentinel if 0 keeps
            // the fast-path size-match check honest for unknown sizes.
            public long   Size        => _e.Size == 0 ? -1 : (long)_e.Size;
            public bool   IsDirectory => _e.IsFolder;

            // SevenZipExtractor doesn't expose a streaming-read API per entry —
            // we have to extract to a stream. For small entries (BIOS, manifests)
            // we buffer into memory so callers can read repeatedly (e.g. compute
            // MD5 then copy to disk). For large entries the caller should prefer
            // ExtractTo(fileStream) which streams directly to disk via the same
            // underlying call.
            public Stream OpenEntryStream()
            {
                var ms = new MemoryStream();
                _e.Extract(ms);
                ms.Position = 0;
                return ms;
            }

            public void ExtractTo(Stream destination) => _e.Extract(destination);
        }
    }
}
