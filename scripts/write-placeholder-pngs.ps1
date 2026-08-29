# Generates the built-in core:iron-ore placeholder PNGs. Run from the repository root.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root "builtin/core/resources/natural/iron-ore"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class PlaceholderPng
{
    public static void Write(string path, int width, int height, byte r, byte g, byte b)
    {
        byte[] raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (width * 4 + 1);
            raw[row] = 0;
            for (int x = 0; x < width; x++)
            {
                int i = row + 1 + x * 4;
                raw[i] = r;
                raw[i + 1] = g;
                raw[i + 2] = b;
                raw[i + 3] = 255;
            }
        }

        using (FileStream output = File.Create(path))
        {
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            WriteChunk(output, "IHDR", Ihdr(width, height));
            WriteChunk(output, "IDAT", Zlib(raw));
            WriteChunk(output, "IEND", new byte[0]);
        }
    }

    private static byte[] Ihdr(int width, int height)
    {
        byte[] data = new byte[13];
        WriteInt(data, 0, width);
        WriteInt(data, 4, height);
        data[8] = 8;
        data[9] = 6;
        return data;
    }

    private static byte[] Zlib(byte[] raw)
    {
        using (MemoryStream buffer = new MemoryStream())
        {
            buffer.WriteByte(0x78);
            buffer.WriteByte(0x9C);
            using (DeflateStream deflate = new DeflateStream(buffer, CompressionMode.Compress, true))
                deflate.Write(raw, 0, raw.Length);
            WriteInt(buffer, unchecked((int)Adler(raw)));
            return buffer.ToArray();
        }
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        WriteInt(stream, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        uint crc = Crc(typeBytes, data);
        WriteInt(stream, unchecked((int)crc));
    }

    private static void WriteInt(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteInt(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static uint Adler(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) + a;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < type.Length; i++)
            crc = CrcByte(crc, type[i]);
        for (int i = 0; i < data.Length; i++)
            crc = CrcByte(crc, data[i]);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }
}
"@

[PlaceholderPng]::Write((Join-Path $dir "icon.png"), 32, 32, 180, 92, 56)
[PlaceholderPng]::Write((Join-Path $dir "top.png"), 16, 16, 196, 108, 64)
[PlaceholderPng]::Write((Join-Path $dir "side.png"), 16, 16, 148, 72, 44)
[PlaceholderPng]::Write((Join-Path $dir "bottom.png"), 16, 16, 96, 52, 32)
Write-Host "Wrote placeholder PNGs to $dir"
