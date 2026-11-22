namespace ClearMeTool;

internal static partial class Firmware
{
    public static byte[] UninitializeMe(SpiImage source, out string report)
    {
        var me = source.Me ?? throw new InvalidDataException("Source image has no ME region.");
        if (source.MeInfo.FptOffset < 0 || source.MeInfo.Partitions.Count == 0)
            throw new InvalidDataException(
                "No $FPT partition table in the ME region. This is not an Intel ME layout this tool can uninitialize.");

        var ftpr = source.MeInfo.Partitions.FirstOrDefault(p => p.Name == "FTPR" && p.Present)
            ?? throw new InvalidDataException("No FTPR code partition. Refusing to erase ME data with nothing left to boot.");

        var output = (byte[])source.Data.Clone();
        var log = new StringBuilder();
        log.AppendLine($"Uninitialize ME {source.MeInfo.Version} in this dump. No donor file.");
        log.AppendLine("Code stays. State partitions are erased to 0xFF so the ME recreates them on first boot.");

        int erasedBytes = 0;
        int erasedParts = 0;
        foreach (var part in source.MeInfo.Partitions)
        {
            if (!part.Present)
            {
                log.AppendLine($"  {part.Name}: empty, skipped.");
                continue;
            }
            if (part.Offset < 0 || part.Length <= 0 || part.Offset + part.Length > me.Size)
                throw new InvalidDataException($"Partition {part.Name} sits outside the ME region.");

            if (!part.IsState)
            {
                log.AppendLine($"  keep {part.Name} at ME+0x{part.Offset:X} ({part.Length:N0} bytes).");
                continue;
            }

            output.AsSpan(me.Base + part.Offset, part.Length).Fill(0xFF);
            erasedBytes += part.Length;
            erasedParts++;
            log.AppendLine($"  erase {part.Name} at ME+0x{part.Offset:X} ({part.Length:N0} bytes).");
        }

        if (erasedParts == 0)
            throw new InvalidDataException("No MFS, FLOG, IVBP or EFFS partition was found, so there was nothing safe to erase.");

        var ftprBefore = source.Data.AsSpan(me.Base + ftpr.Offset, ftpr.Length);
        var ftprAfter = output.AsSpan(me.Base + ftpr.Offset, ftpr.Length);
        if (!ftprBefore.SequenceEqual(ftprAfter))
            throw new InvalidOperationException("FTPR changed during uninitialize. Output discarded.");

        AssertUntouched(source.Data, output, source.Descriptor, "Descriptor", log);
        AssertUntouched(source.Data, output, source.Bios, "BIOS", log);
        AssertUntouched(source.Data, output, source.Gbe, "GbE", log);

        log.AppendLine();
        log.AppendLine($"Erased {erasedParts} state partition(s), {erasedBytes:N0} bytes.");
        log.AppendLine("FTPR was not modified. BIOS serial / UUID / setup password were not modified by this step.");
        log.AppendLine("First boot recreates the ME filesystem. Firmware TPM (PTT) keys from the old ME are gone.");
        log.AppendLine("This is an erased filesystem, not an Intel FIT factory RGN. If the board boot-loops, write the original dump back.");
        report = log.ToString();
        return output;
    }

    public const string OpenFilter =
        "SPI / firmware (*.bin;*.rom;*.fd;*.rgn;*.bio)|*.bin;*.rom;*.fd;*.rgn;*.bio|All files (*.*)|*.*";

    public const string SaveFilter = "Binary (*.bin;*.rom)|*.bin;*.rom|All files (*.*)|*.*";

    /// <summary>
    /// Invalidates AMI setup-password variables inside the BIOS region and zeroes their payload.
    /// Other NVAR records, including SerialNUM, are not written.
    /// </summary>
    public static int ClearAmiSetupPassword(byte[] image, FlashRegion bios, out string report)
    {
        int start = bios.Base;
        int limit = bios.Base + bios.Size;
        int cleared = 0;
        var notes = new StringBuilder();
        var visited = new bool[bios.Size];

        for (int i = start; i + 0x18 <= limit; i++)
        {
            if (visited[i - start])
                continue;
            if (!IsNvarHeader(image, i, limit, out _))
                continue;

            int off = i;
            while (off + 0x18 <= limit && !visited[off - start] && IsNvarHeader(image, off, limit, out int size))
            {
                for (int b = 0; b < size && off - start + b < visited.Length; b++)
                    visited[off - start + b] = true;

                if (TryReadNvarName(image, off, size, out var name) && IsPasswordVariable(name))
                {
                    byte flags = image[off + 9];
                    image[off + 9] = (byte)(flags & ~0x80);
                    int zeroed = ZeroNvarPayload(image, off, size);
                    cleared++;
                    notes.AppendLine(
                        $"  {name} at file offset 0x{off:X}: valid bit cleared, {zeroed} payload bytes set to 0.");
                }

                off += size;
            }
        }

        if (cleared == 0)
        {
            report = "No AMI setup-password variables (AMITSESetup / AdminPw / UserPw) found in the BIOS region.";
            return 0;
        }

        report = $"Cleared {cleared} password variable(s).\n" + notes.ToString();
        return cleared;
    }

    public static string Compare(string pathA, string pathB)
    {
        var a = File.ReadAllBytes(pathA);
        var b = File.ReadAllBytes(pathB);
        var log = new StringBuilder();
        log.AppendLine($"A: {pathA}");
        log.AppendLine($"   size {a.Length:N0}  SHA-256 {Sha256(a)}");
        log.AppendLine($"B: {pathB}");
        log.AppendLine($"   size {b.Length:N0}  SHA-256 {Sha256(b)}");
        log.AppendLine();

        if (a.Length == b.Length && a.AsSpan().SequenceEqual(b))
        {
            log.AppendLine("Files are identical.");
            return log.ToString();
        }

        int diff = CountDiff(a, b);
        log.AppendLine($"{diff:N0} bytes differ.");

        bool mapA = TryParseRegions(a, out var regionsA, out _);
        bool mapB = TryParseRegions(b, out var regionsB, out _);
        if (mapA && mapB)
        {
            log.AppendLine();
            log.AppendLine("Per-region:");
            int rows = Math.Max(regionsA.Count, regionsB.Count);
            for (int i = 0; i < rows; i++)
            {
                var ra = i < regionsA.Count ? regionsA[i] : null;
                var rb = i < regionsB.Count ? regionsB[i] : null;
                string name = ra?.Name ?? rb?.Name ?? $"Region{i}";
                if (ra is not { Enabled: true } || rb is not { Enabled: true })
                {
                    log.AppendLine($"  {name}: not enabled in both");
                    continue;
                }
                if (ra.Base != rb.Base || ra.Size != rb.Size)
                {
                    log.AppendLine($"  {name}: layout differs (A {ra.RangeText}, B {rb.RangeText})");
                    continue;
                }
                int changed = 0;
                for (int n = 0; n < ra.Size; n++)
                    if (a[ra.Base + n] != b[rb.Base + n]) changed++;
                log.AppendLine(changed == 0
                    ? $"  {name}: identical ({ra.Size:N0} bytes)"
                    : $"  {name}: {changed:N0} bytes differ");
            }
        }
        else
        {
            log.AppendLine();
            log.AppendLine(mapA
                ? "B has no Intel flash descriptor, so regions were not compared."
                : mapB
                    ? "A has no Intel flash descriptor, so regions were not compared."
                    : "Neither file has an Intel flash descriptor. Byte compare only.");
        }

        log.AppendLine();
        log.AppendLine("Difference ranges (file offsets):");
        int shown = 0;
        int rangeStart = -1;
        int extra = 0;
        int span = Math.Max(a.Length, b.Length);
        for (int i = 0; i <= span; i++)
        {
            bool different = i < span && (i >= a.Length || i >= b.Length || a[i] != b[i]);
            if (different && rangeStart < 0)
                rangeStart = i;
            if (!different && rangeStart >= 0)
            {
                if (shown < 24)
                {
                    log.AppendLine($"  0x{rangeStart:X}–0x{i - 1:X}  ({i - rangeStart:N0} bytes)");
                    shown++;
                }
                else
                {
                    extra++;
                }
                rangeStart = -1;
            }
        }
        if (extra > 0)
            log.AppendLine($"  … {extra} more ranges not shown.");

        return log.ToString();
    }

    private static bool IsPasswordVariable(string name)
    {
        return name is "AMITSESetup" or "AdminPw" or "UserPw";
    }
}
