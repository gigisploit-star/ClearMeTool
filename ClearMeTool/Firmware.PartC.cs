namespace ClearMeTool;

internal static partial class Firmware
{
    private static bool IsNvarHeader(byte[] image, int off, int limit, out int size)
    {
        size = 0;
        if (off < 0 || off + 10 > limit)
            return false;
        if (image[off] != (byte)'N' || image[off + 1] != (byte)'V' || image[off + 2] != (byte)'A' || image[off + 3] != (byte)'R')
            return false;

        size = BitConverter.ToUInt16(image, off + 4);
        if (size < 0x18 || off + size > limit)
            return false;

        int next = off + size;
        if (next == limit || image[next] == 0xFF)
            return true;
        return next + 4 <= limit
            && image[next] == (byte)'N'
            && image[next + 1] == (byte)'V'
            && image[next + 2] == (byte)'A'
            && image[next + 3] == (byte)'R';
    }

    private static bool TryReadNvarName(byte[] image, int off, int size, out string name)
    {
        name = "";
        byte flags = image[off + 9];
        if ((flags & 0x02) == 0)
            return false;

        // This AMI generation stores a one-byte GUID index, then an ASCII name.
        int nameAt = off + 11;
        int end = off + size;
        if (nameAt >= end)
            return false;

        var chars = new StringBuilder();
        for (int i = nameAt; i < end; i++)
        {
            byte c = image[i];
            if (c == 0)
                break;
            if (c is < 32 or > 126)
                return false;
            chars.Append((char)c);
            if (chars.Length > 64)
                return false;
        }
        if (chars.Length == 0)
            return false;
        name = chars.ToString();
        return true;
    }

    private static int ZeroNvarPayload(byte[] image, int off, int size)
    {
        int nameAt = off + 11;
        int end = off + size;
        int data = nameAt;
        while (data < end && image[data] != 0)
            data++;
        if (data < end)
            data++;
        int zeroed = 0;
        for (int i = data; i < end; i++)
        {
            if (image[i] != 0)
            {
                image[i] = 0;
                zeroed++;
            }
        }
        return zeroed;
    }

    public static string Sha256(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    public static string ShortHash(ReadOnlySpan<byte> data) => Sha256(data)[..16];

    private static void AssertUntouched(byte[] before, byte[] after, FlashRegion? region, string name, StringBuilder log)
    {
        if (region == null || !region.Enabled)
        {
            log.AppendLine($"{name}: not present.");
            return;
        }

        bool same = before.AsSpan(region.Base, region.Size)
            .SequenceEqual(after.AsSpan(region.Base, region.Size));
        if (!same)
            throw new InvalidOperationException($"Internal error: {name} region was modified. Output discarded.");
        log.AppendLine($"{name}: unchanged ({region.Size:N0} bytes, {region.RangeText}).");
    }

    private static void EnsureCodeLayoutMatches(MeDetails source, MeDetails clean)
    {
        var srcCode = source.Partitions.Where(p => p.IsCode && p.Present).ToList();
        var cleanCode = clean.Partitions.Where(p => p.IsCode && p.Present).ToList();
        if (srcCode.Count == 0)
            throw new InvalidDataException("Could not find code partitions (FTPR) in your ME. Use full ME replace instead.");

        foreach (var part in srcCode)
        {
            var other = cleanCode.FirstOrDefault(p => p.Name == part.Name);
            if (other == null || other.Offset != part.Offset || other.Length != part.Length)
                throw new InvalidDataException(
                    $"Code partition {part.Name} does not line up between the two ME images. " +
                    "State-only mode is refused. Use full ME replace only if you accept a different ME firmware body.");
        }
    }

    private static bool IsInsideCode(MeDetails info, int offset)
    {
        foreach (var part in info.Partitions)
        {
            if (!part.IsCode || !part.Present)
                continue;
            if (offset >= part.Offset && offset < part.Offset + part.Length)
                return true;
        }
        return false;
    }

    public static bool TryParseRegions(byte[] data, out List<FlashRegion> regions, out string error)
    {
        regions = [];
        error = "";
        if (!HasDescriptor(data))
        {
            error = "No Intel flash descriptor signature (5A A5 F0 0F) at offset 0x10.";
            return false;
        }

        uint flmap0 = BitConverter.ToUInt32(data, 0x14);
        int count = (int)(flmap0 & 0xFF) + 1;
        int frba = (int)(((flmap0 >> 16) & 0xFF) << 4);
        if (count is < 1 or > 10)
        {
            error = $"Unreasonable region count ({count}) in FLMAP0.";
            return false;
        }
        if (frba + count * 4 > data.Length)
        {
            error = "FLREG table runs past the end of the file.";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            uint flreg = BitConverter.ToUInt32(data, frba + i * 4);
            int base_ = (int)((flreg & 0x7FFF) << 12);
            int limit = (int)(((flreg >> 16) & 0x7FFF) << 12) | 0xFFF;
            bool enabled = (flreg & 0x7FFF) != 0x7FFF || ((flreg >> 16) & 0x7FFF) != 0x7FFF;
            string name = i < RegionNames.Length ? RegionNames[i] : $"Region{i}";
            regions.Add(new FlashRegion
            {
                Index = i,
                Name = name,
                Base = base_,
                Limit = limit,
                Enabled = enabled && base_ <= limit
            });
        }
        return true;
    }

    public static MeDetails ParseMe(byte[] image, FlashRegion me)
    {
        int fpt = FindSignature(image, me.Base, me.Size, "$FPT"u8);
        if (fpt < 0)
            return new MeDetails();

        int rel = fpt - me.Base;
        // $FPT header: 4-byte magic, then number of partitions at +0x20 or similar; observed layouts vary.
        // We walk the table entries that follow.
        var parts = new List<MePartition>();
        int entry = fpt + 0x30; // common for CSME 11
        int maxEntries = 32;
        for (int i = 0; i < maxEntries && entry + 0x20 <= me.Base + me.Size; i++)
        {
            // Name is 4 ASCII bytes
            if (image[entry] == 0xFF && image[entry + 1] == 0xFF)
                break;
            string name = Encoding.ASCII.GetString(image, entry, 4).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => c < 32 || c > 126))
                break;
            uint offset = BitConverter.ToUInt32(image, entry + 4);
            uint length = BitConverter.ToUInt32(image, entry + 8);
            if (offset + length > (uint)me.Size && length > 0)
            {
                // some tables use different field sizes; skip invalid
                entry += 0x20;
                continue;
            }
            parts.Add(new MePartition
            {
                Name = name,
                Offset = (int)offset,
                Length = (int)length
            });
            entry += 0x20;
        }

        string version = "unknown";
        var ftpr = parts.FirstOrDefault(p => p.Name == "FTPR" && p.Present);
        if (ftpr != null)
        {
            int mn2 = FindSignature(image, me.Base + ftpr.Offset, ftpr.Length, "$MN2"u8);
            version = ReadVersion(image, mn2) ?? version;
        }
        if (version == "unknown")
        {
            int any = FindSignature(image, me.Base, me.Size, "$MN2"u8);
            version = ReadVersion(image, any) ?? version;
        }

        return new MeDetails
        {
            FptOffset = rel,
            Version = version,
            Partitions = parts
        };
    }

    private static string? ReadVersion(byte[] image, int mn2)
    {
        // $MN2 manifest on ME 11+: four little-endian uint16 values at +0x08.
        // Observed on an M710t dump as 11.6.1.1142.
        if (mn2 < 0 || mn2 + 16 > image.Length)
            return null;
        ushort major = BitConverter.ToUInt16(image, mn2 + 8);
        ushort minor = BitConverter.ToUInt16(image, mn2 + 10);
        ushort hotfix = BitConverter.ToUInt16(image, mn2 + 12);
        ushort build = BitConverter.ToUInt16(image, mn2 + 14);
        if (major is 0 or > 30)
            return null;
        return $"{major}.{minor}.{hotfix}.{build}";
    }

    private static int FindSignature(byte[] data, int start, int length, ReadOnlySpan<byte> sig)
    {
        int end = Math.Min(data.Length, start + length) - sig.Length;
        for (int i = start; i <= end; i++)
        {
            if (data.AsSpan(i, sig.Length).SequenceEqual(sig))
                return i;
        }
        return -1;
    }

    private static bool HasDescriptor(byte[] data)
    {
        return data.Length > 0x14
            && data[0x10] == 0x5A && data[0x11] == 0xA5
            && data[0x12] == 0xF0 && data[0x13] == 0x0F;
    }

    private static bool LooksLikeMe(byte[] me)
    {
        int window = Math.Min(me.Length, 0x1000);
        return FindSignature(me, 0, window, "$FPT"u8) >= 0;
    }

    private static byte[] Slice(byte[] image, FlashRegion region)
    {
        var slice = new byte[region.Size];
        Buffer.BlockCopy(image, region.Base, slice, , 0, region.Size);
        return slice;
    }

    private static void Fill(byte[] data, int offset, int length, byte value)
    {
        data.AsSpan(offset, length).Fill(value);
    }

    private static int CountDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int diff = Math.Abs(a.Length - b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) diff++;
        return diff;
    }
}
