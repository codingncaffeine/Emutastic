using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Minimal reader for the PARAM.SFO metadata record present in PlayStation 3 content.
    /// Exposes the title and serial (title id) used to name and key a library entry.
    /// Returns whatever it can parse; never throws.
    /// </summary>
    public static class ParamSfo
    {
        // Format-field codes used in the index table.
        private const ushort FmtUtf8Special = 0x0004;
        private const ushort FmtUtf8        = 0x0204;
        private const ushort FmtUInt32      = 0x0404;

        public static Dictionary<string, string> Read(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                byte[] d = File.ReadAllBytes(path);
                // Header magic "\0PSF" + minimum header length.
                if (d.Length < 0x14 || d[0] != 0x00 || d[1] != 0x50 || d[2] != 0x53 || d[3] != 0x46)
                    return result;

                uint keyTable  = BitConverter.ToUInt32(d, 0x08);
                uint dataTable = BitConverter.ToUInt32(d, 0x0C);
                uint count     = BitConverter.ToUInt32(d, 0x10);

                for (uint i = 0; i < count; i++)
                {
                    int e = 0x14 + (int)i * 16;
                    if (e + 16 > d.Length) break;

                    ushort keyOffset = BitConverter.ToUInt16(d, e);
                    ushort fmt       = BitConverter.ToUInt16(d, e + 2);
                    uint   dataLen   = BitConverter.ToUInt32(d, e + 4);
                    uint   dataOffset= BitConverter.ToUInt32(d, e + 12);

                    int keyPos = (int)keyTable + keyOffset;
                    if (keyPos < 0 || keyPos >= d.Length) break;
                    int keyEnd = keyPos;
                    while (keyEnd < d.Length && d[keyEnd] != 0) keyEnd++;
                    string key = Encoding.ASCII.GetString(d, keyPos, keyEnd - keyPos);

                    int dataPos = (int)dataTable + (int)dataOffset;
                    if (dataPos < 0 || dataPos > d.Length) continue;

                    string value;
                    if (fmt == FmtUInt32)
                    {
                        value = dataPos + 4 <= d.Length ? BitConverter.ToUInt32(d, dataPos).ToString() : "";
                    }
                    else // utf8 string (regular or special)
                    {
                        int len = (int)dataLen;
                        if (dataPos + len > d.Length) len = d.Length - dataPos;
                        if (len < 0) len = 0;
                        int z = Array.IndexOf(d, (byte)0, dataPos, len);
                        if (z >= 0) len = z - dataPos;
                        value = Encoding.UTF8.GetString(d, dataPos, Math.Max(0, len));
                    }
                    result[key] = value;
                }
            }
            catch { /* return whatever parsed */ }
            return result;
        }

        public static string? Title(string path)
            => Read(path).TryGetValue("TITLE", out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;

        public static string? Serial(string path)
            => Read(path).TryGetValue("TITLE_ID", out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}
