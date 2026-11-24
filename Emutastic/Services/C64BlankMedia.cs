using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Emutastic.Services
{
    /// <summary>
    /// New, empty Commodore 64 media to save programs to: a floppy (a D64, formatted as the 1541's DOS formats
    /// one, with no files) or a tape (a TAP with nothing on it). Kernal64 starts an empty disk at READY and a
    /// blank tape with RECORD and PLAY down, and keeps what is written in the save folder.
    /// (Hard drives are CMD HD drives, which Kernal64 does not emulate yet.)
    /// </summary>
    public static class C64BlankMedia
    {
        public const string StorageFolder = "Commodore 64 Storage", DiskFolder = "Floppy Disks", TapeFolder = "Tapes";

        /// <summary>
        /// Where the media made from a game's card go, in the app's data folder (beside its configuration):
        /// Commodore 64 Storage / the game / the kind of medium (Floppy Disks, Tapes).
        /// </summary>
        public static string FolderFor(string gameTitle, string kind) =>
            AppPaths.GetFolder(StorageFolder, SafeName(gameTitle, "Commodore 64"), kind);

        // The file is named "GAME - NAME": Kernal64 keeps what is written to a medium in the save folder under
        // the medium's file name, so two games' media must not share a name.

        /// <summary>Writes "GAME - NAME.d64" (or "... NAME 2.d64" when taken) and returns its path.</summary>
        public static string CreateDisk(string gameTitle, string name) =>
            Write(FolderFor(gameTitle, DiskFolder), FileStem(gameTitle, name), ".d64", FormattedD64(name));

        /// <summary>Writes "GAME - NAME.tap" (or "... NAME 2.tap") and returns its path.</summary>
        public static string CreateTape(string gameTitle, string name) =>
            Write(FolderFor(gameTitle, TapeFolder), FileStem(gameTitle, name), ".tap",
                Encoding.ASCII.GetBytes("C64-TAPE-RAW").Concat(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }).ToArray());

        private static string FileStem(string gameTitle, string name) =>
            SafeName(gameTitle, "Commodore 64") + " - " + SafeName(name, "Blank");

        private static string SafeName(string name, string fallback)
        {
            string safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
            return safe.Length == 0 ? fallback : safe;
        }

        private static string Write(string folder, string name, string ext, byte[] data)
        {
            string safe = SafeName(name, "Blank");
            string path = Path.Combine(folder, safe + ext);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{safe} {n}{ext}");
            File.WriteAllBytes(path, data);
            return path;
        }

        private static int SectorsOnTrack(int t) => t <= 17 ? 21 : t <= 24 ? 19 : t <= 30 ? 18 : 17;

        /// <summary>
        /// A 35-track D64 as the DOS's NEW command leaves it: the BAM at track 18 sector 0 (every sector free but
        /// 18/0 and 18/1, the disk name and ID), an empty directory at 18/1 (1541 User's Guide, the BAM format).
        /// </summary>
        private static byte[] FormattedD64(string name)
        {
            int total = Enumerable.Range(1, 35).Sum(SectorsOnTrack);
            var img = new byte[total * 256];
            int Offset(int track, int sector) => (Enumerable.Range(1, track - 1).Sum(SectorsOnTrack) + sector) * 256;
            int b = Offset(18, 0);
            img[b] = 18; img[b + 1] = 1; img[b + 2] = 0x41;
            for (int t = 1; t <= 35; t++)
            {
                int n = SectorsOnTrack(t);
                uint bits = (1u << n) - 1;
                if (t == 18) bits &= ~3u;
                img[b + 4 * t] = (byte)System.Numerics.BitOperations.PopCount(bits);
                img[b + 4 * t + 1] = (byte)bits; img[b + 4 * t + 2] = (byte)(bits >> 8); img[b + 4 * t + 3] = (byte)(bits >> 16);
            }
            // Disk name (PETSCII upper case; what the C64 cannot show becomes a space), padded with $A0; ID "01"; DOS "2A".
            for (int i = 0; i < 16; i++)
            {
                char c = i < name.Length ? char.ToUpperInvariant(name[i]) : '\0';
                img[b + 0x90 + i] = c == '\0' ? (byte)0xA0 : c >= 0x20 && c <= 0x5D ? (byte)c : (byte)' ';
            }
            img[b + 0xA0] = 0xA0; img[b + 0xA1] = 0xA0;
            img[b + 0xA2] = (byte)'0'; img[b + 0xA3] = (byte)'1'; img[b + 0xA4] = 0xA0;
            img[b + 0xA5] = (byte)'2'; img[b + 0xA6] = (byte)'A';
            for (int i = 0xA7; i <= 0xAA; i++) img[b + i] = 0xA0;
            int d = Offset(18, 1);
            img[d] = 0; img[d + 1] = 0xFF;
            return img;
        }
    }
}
