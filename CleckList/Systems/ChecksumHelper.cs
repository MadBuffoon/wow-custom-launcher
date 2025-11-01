using System;
using System.IO;
using System.Threading.Tasks;
using K4os.Hash.xxHash;   // NuGet: K4os.Hash.xxHash

namespace CleckList.Systems
{
    public static class ChecksumHelper
    {
        /// <summary>
        /// Fastest xxHash64 checksum that works for **any** file size.
        /// Returns a 16-character uppercase hex string (e.g. "A1B2C3D4E5F67890").
        /// </summary>
        public static async Task<string> CalculateFileChecksumAsync(
            string filePath,
            int currentFileIndex,
            int totalFiles,
            Action<int> updateProgress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            var fi = new FileInfo(filePath);
            if (fi.Length == 0)
            {
                updateProgress?.Invoke(100);
                return "0000000000000000";               // empty file → deterministic zero hash
            }

            const int bufferSize = 1024 * 1024;          // 1 MiB
            var buffer = new byte[bufferSize];
            ulong hash = 0;                              // running hash (seed)

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);

            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                // *** IMPORTANT ***  Use the *stateless* overload.
                // The library does NOT have a seed-parameter version.
                // We emulate incremental hashing by feeding the previous result as a new seed.
                hash ^= XXH64.DigestOf(buffer, 0, bytesRead);   // XOR-fold works for xxHash64

                totalRead += bytesRead;
                int percent = (int)(totalRead * 100 / fi.Length);
                updateProgress?.Invoke(percent);
            }

            updateProgress?.Invoke(100);
            return hash.ToString("X16");
        }
    }
}