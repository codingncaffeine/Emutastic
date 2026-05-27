using System.IO;
using System.Linq;

namespace Emutastic
{
    public static class FileNameHelper
    {
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        public static string SanitizeFileName(string s)
            => new string(s.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
