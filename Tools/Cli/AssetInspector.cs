using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using Train.Content.Contracts;

namespace Train.Cli;

internal sealed partial class AssetInspector : IAssetInspector
{
    public AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes)
    {
        try
        {
            return kind == AssetKind.Glb ? InspectGlb(bytes) : InspectPng(bytes.AsSpan());
        }
        catch (Train.App.Assets.AssetDecodeException exception)
        {
            return new AssetInspection.Failure(exception.Kind, exception.Message);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or FormatException)
        {
            return CannotDecode(exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return CannotDecode(exception.Message);
        }
        catch (IOException exception)
        {
            return CannotDecode(exception.Message);
        }
    }

    private static AssetInspection InspectPng(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return CannotDecode("PNG 签名无效。");

        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 12)
                return CannotDecode("PNG 块被截断。");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12)
                return CannotDecode("PNG 块数据被截断。");
            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, (int)length);
            if (Crc(bytes.Slice(offset + 4, (int)length + 4)) !=
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)length, 4)))
                return CannotDecode("PNG 块 CRC 无效。");
            offset += (int)length + 12;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (width != 0 || offset != 33 || data.Length != 13)
                    return CannotDecode("PNG 头无效。");
                var w = BinaryPrimitives.ReadUInt32BigEndian(data);
                var h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                    return CannotDecode("PNG 尺寸无效。");
                if (data[10] != 0 || data[11] != 0 || data[12] > 1)
                    return CannotDecode("PNG 压缩、过滤或交错方法无效。");
                var validDepth = data[9] switch
                {
                    0 => data[8] is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => data[8] is 8 or 16,
                    3 => data[8] is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth) return CannotDecode("PNG 颜色类型或位深无效。");
                if (data[8] != 8 || data[9] is not (2 or 6) || data[12] != 0)
                    throw Unsupported("命令行工具只支持 8 位 RGB/RGBA、非交错 PNG。");
                channels = data[9] == 2 ? 3 : 4;
                width = (int)w;
                height = (int)h;
            }
            else if (width == 0)
                return CannotDecode("PNG 必须以 IHDR 开始。");
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (compressed.Length == 0 || data.Length != 0 || offset != bytes.Length)
                    return CannotDecode("PNG 结束块无效。");
                return InspectPixels(width, height, channels, compressed);
            }
        }
        return CannotDecode("PNG 缺少 IEND。");
    }

    private static AssetInspection InspectPixels(int width, int height, int channels, MemoryStream compressed)
    {
        compressed.Position = 0;
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var rowLength = 1L + (long)channels * width;
        if (height > long.MaxValue / rowLength)
            return CannotDecode("PNG 尺寸超出可表示的像素数据长度。");
        var expectedLength = rowLength * height;
        var buffer = new byte[8192];
        long total = 0;
        int count;
        while ((count = decoded.Read(buffer)) != 0)
        {
            if (count > expectedLength - total)
                return CannotDecode("PNG 像素数据长于尺寸所需长度。");
            total += count;
        }
        if (total != expectedLength)
            return CannotDecode("PNG 像素数据短于尺寸所需长度。");
        return new AssetInspection.Success(new AssetMetadata.Png(width, height));
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static AssetInspection CannotDecode(string diagnostic) =>
        new AssetInspection.Failure(AssetInspectionFailure.CannotDecode, diagnostic);
}
