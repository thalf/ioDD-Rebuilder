using System.Security.Cryptography;

namespace ioDDRebuilder;

// Read-only verification is independent of storage discovery and formatting.
internal static class FileVerification
{
    internal static void Verify(string source, string destination, CancellationToken token)
    {
        byte[] Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
        }
        if (!Hash(source).SequenceEqual(Hash(destination)))
            throw new IOException("Checksum mismatch: " + Path.GetFileName(source));
    }
}
