# Generates visible-orientation PNGs for core:refit-marker and core:refit-replacement.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class RefitMarkerPng
{
    public static void WriteSolid(string path, int width, int height, byte r, byte g, byte b)
    {
        Write(path, Fill(width, height, r, g, b), width, height);
    }

    public static void WriteArrowSide(string path, int width, int height, byte br, byte bg, byte bb, byte ar, byte ag, byte ab)
    {
        var raw = Fill(width, height, br, bg, bb);
        int mid = height / 2;
        int thickness = Math.Max(1, height / 8);
        for (int y = 0; y < height; y++)
        {
            int row = y * (width * 4 + 1);
            if (Math.Abs(y - mid) <= thickness)
            {
                for (int x = 2; x < width - 3; x++)
                    Set(raw, row, x, ar, ag, ab);
            }
            int head = width - 4;
            int spread = Math.Abs(y - mid);
            if (spread <= 5)
            {
                for (int x = head - (5 - spread); x <= head; x++)
                {
                    if (x >= 0 && x < width)
                        Set(raw, row, x, ar, ag, ab);
                }
            }
        }
        Write(path, raw, width, height);
    }

    private static byte[] Fill(int width, int height, byte r, byte g, byte b)
    {
        byte[] raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (width * 4 + 1);
            raw[row] = 0;
            for (int x = 0; x < width; x++)
                Set(raw, row, x, r, g, b);
        }
        return raw;
    }

    private static void Set(byte[] raw, int row, int x, byte r, byte g, byte b)
    {
        int i = row + 1 + x * 4;
        raw[i] = r;
        raw[i + 1] = g;
        raw[i + 2] = b;
        raw[i + 3] = 255;
    }

    private static void Write(string path, byte[] raw, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
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

$marker = Join-Path $root "builtin/core/resources/voxels/refit-marker"
$repl = Join-Path $root "builtin/core/resources/voxels/refit-replacement"
New-Item -ItemType Directory -Force -Path $marker, $repl | Out-Null
[RefitMarkerPng]::WriteSolid((Join-Path $marker "icon.png"), 32, 32, 40, 200, 255)
[RefitMarkerPng]::WriteSolid((Join-Path $marker "top.png"), 16, 16, 255, 220, 40)
[RefitMarkerPng]::WriteArrowSide((Join-Path $marker "side.png"), 16, 16, 20, 120, 220, 255, 255, 255)
[RefitMarkerPng]::WriteSolid((Join-Path $marker "bottom.png"), 16, 16, 20, 40, 90)
[RefitMarkerPng]::WriteSolid((Join-Path $repl "icon.png"), 32, 32, 220, 80, 40)
[RefitMarkerPng]::WriteSolid((Join-Path $repl "top.png"), 16, 16, 240, 90, 40)
[RefitMarkerPng]::WriteSolid((Join-Path $repl "side.png"), 16, 16, 180, 50, 30)
[RefitMarkerPng]::WriteSolid((Join-Path $repl "bottom.png"), 16, 16, 90, 30, 20)

$imports = @(
    @{ Path = Join-Path $marker "icon.png.import"; Uid = "uid://c60refitmrkicon" },
    @{ Path = Join-Path $marker "top.png.import"; Uid = "uid://c60refitmrktop" },
    @{ Path = Join-Path $marker "side.png.import"; Uid = "uid://c60refitmrkside" },
    @{ Path = Join-Path $marker "bottom.png.import"; Uid = "uid://c60refitmrkbot" },
    @{ Path = Join-Path $repl "icon.png.import"; Uid = "uid://c60refitreplicon" },
    @{ Path = Join-Path $repl "top.png.import"; Uid = "uid://c60refitrepltop" },
    @{ Path = Join-Path $repl "side.png.import"; Uid = "uid://c60refitreplside" },
    @{ Path = Join-Path $repl "bottom.png.import"; Uid = "uid://c60refitreplbot" }
)
foreach ($item in $imports) {
    Set-Content -LiteralPath $item.Path -Value "[remap]`r`n`r`nimporter=`"keep`"`r`nuid=`"$($item.Uid)`"" -NoNewline
}
Write-Host "Wrote refit marker PNGs"
Get-ChildItem $marker, $repl | Format-Table Name, Length
