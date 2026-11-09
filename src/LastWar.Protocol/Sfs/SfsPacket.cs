using System.Buffers.Binary;
using System.IO.Compression;

namespace LastWar.Protocol.Sfs;

/// <summary>
/// The outer frame around an encoded SFSObject.
///
/// Layout: one header byte of flags, an optional 2-byte routing id when Forward is set,
/// then a length (int16, or int32 when BigSized), then the body. When Compressed and
/// UseZstd are both set the length is followed by a 4-byte uncompressed size.
///
/// "Encrypted" is generous: the body is XORed with the little-endian bytes of its own
/// length, so it is obfuscation, not cryptography. Faithfully reproduced because the
/// server requires it.
/// </summary>
public static class SfsPacket
{
    [Flags]
    public enum Header : byte
    {
        Binary = 0x80,
        Encrypted = 0x40,
        Compressed = 0x20,
        /// <summary>Named LZ4 by the protocol; the payload is actually Zstandard.</summary>
        UseZstd = 0x10,
        BigSized = 0x08,
        Forward = 0x04,
    }

    public const int CompressionThreshold = 1024;
    public const int MaxFrameSize = 64 << 20;

    static byte[] Xor(ReadOnlySpan<byte> data)
    {
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, (uint)data.Length);
        var outp = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) outp[i] = (byte)(data[i] ^ key[i % 4]);
        return outp;
    }

    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        var compressed = false;
        var payload = body.ToArray();
        if (payload.Length > CompressionThreshold)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(payload);
            payload = ms.ToArray();
            compressed = true;
        }

        var encrypted = Xor(payload);
        var big = encrypted.Length > ushort.MaxValue;
        var header = Header.Binary | Header.Encrypted;
        if (compressed) header |= Header.Compressed;
        if (big) header |= Header.BigSized;

        using var outp = new MemoryStream();
        outp.WriteByte((byte)header);
        Span<byte> len = stackalloc byte[4];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)encrypted.Length);
            outp.Write(len);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(len[..2], (ushort)encrypted.Length);
            outp.Write(len[..2]);
        }
        outp.Write(encrypted);
        return outp.ToArray();
    }

    public static async Task<byte[]> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var header = (Header)await ReadByteAsync(s, ct).ConfigureAwait(false);

        if (header.HasFlag(Header.Forward))
            await ReadExactAsync(s, 2, ct).ConfigureAwait(false);   // routing id, unused

        uint length = header.HasFlag(Header.BigSized)
            ? BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(s, 4, ct).ConfigureAwait(false))
            : BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(s, 2, ct).ConfigureAwait(false));

        if (length > MaxFrameSize)
            throw new InvalidDataException($"frame body too large: {length} bytes");

        var zstd = header.HasFlag(Header.Compressed) && header.HasFlag(Header.UseZstd);
        var uncompressed = 0u;
        if (zstd)
        {
            uncompressed = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(s, 4, ct).ConfigureAwait(false));
            if (uncompressed > MaxFrameSize)
                throw new InvalidDataException($"uncompressed length too large: {uncompressed}");
        }

        var body = await ReadExactAsync(s, (int)length, ct).ConfigureAwait(false);
        if (header.HasFlag(Header.Encrypted)) body = Xor(body);
        if (!header.HasFlag(Header.Compressed)) return body;

        if (zstd)
        {
            using var dec = new ZstdSharp.Decompressor();
            return dec.Unwrap(body, (int)uncompressed).ToArray();
        }

        using var src = new MemoryStream(body);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var outp = new MemoryStream();
        await z.CopyToAsync(outp, ct).ConfigureAwait(false);
        if (outp.Length > MaxFrameSize) throw new InvalidDataException("inflated frame too large");
        return outp.ToArray();
    }

    static async Task<byte> ReadByteAsync(Stream s, CancellationToken ct) =>
        (await ReadExactAsync(s, 1, ct).ConfigureAwait(false))[0];

    static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            var r = await s.ReadAsync(buf.AsMemory(got, n - got), ct).ConfigureAwait(false);
            if (r == 0) throw new EndOfStreamException($"connection closed after {got} of {n} bytes");
            got += r;
        }
        return buf;
    }
}
