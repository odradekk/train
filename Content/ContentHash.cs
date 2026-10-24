using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Train.Content;

internal static class ContentHash
{
    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static string FromCanonicalJson(Action<Utf8JsonWriter> write)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var writer = new Utf8JsonWriter(new HashBuffer(hash)))
            write(writer);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class HashBuffer(IncrementalHash hash) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];

        public void Advance(int count) => hash.AppendData(_buffer.AsSpan(0, count));

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
