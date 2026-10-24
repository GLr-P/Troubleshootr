using System.Text;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Crashes;

/// <summary>
/// Reads the header of a kernel crash dump (C:\Windows\Minidump\*.dmp): the bugcheck code and its four parameters, and
/// for 64-bit small dumps the loaded-driver list, so the crash address can be matched to a driver like BlueScreenView does.
/// Layouts: DUMP_HEADER32/64 and TRIAGE_DUMP (Windows SDK / WDK headers). Every offset is bounds-checked; the driver
/// list is only trusted when every entry it yields looks like a real driver.
/// </summary>
public static class DumpParser
{
    /// <summary>Reads count bytes at offset, or fewer at the end of the file.</summary>
    public delegate byte[] ReadAt(long offset, int count);

    private const int Header64Size = 0x2000;
    private const int Header32Size = 0x1000;

    public static CrashDump Parse(ReadAt read, long length, CrashDump into)
    {
        var h = read(0, 0x60);
        if (h.Length < 0x60 || Encoding.ASCII.GetString(h, 0, 4) != "PAGE")
        {
            into.ParseError = "not a kernel dump file";
            return into;
        }
        var valid = Encoding.ASCII.GetString(h, 4, 4);
        if (valid == "DU64")
        {
            into.BugCheck = BitConverter.ToUInt32(h, 0x38);
            into.Parameters = [BitConverter.ToUInt64(h, 0x40), BitConverter.ToUInt64(h, 0x48), BitConverter.ToUInt64(h, 0x50), BitConverter.ToUInt64(h, 0x58)];
            into.Driver = FaultingDriver(read, length, into.BugCheck.Value, into.Parameters);
        }
        else if (valid == "DUMP")
        {
            into.BugCheck = BitConverter.ToUInt32(h, 0x20);
            into.Parameters = [BitConverter.ToUInt32(h, 0x24), BitConverter.ToUInt32(h, 0x28), BitConverter.ToUInt32(h, 0x2C), BitConverter.ToUInt32(h, 0x30)];
        }
        else into.ParseError = $"unknown dump format ({valid})";
        return into;
    }

    private static string? FaultingDriver(ReadAt read, long length, uint code, List<ulong> ps)
    {
        if (BugChecks.AddressParameter(code) is not { } index || ps[index] is var address && address < 0xFFFF800000000000UL) return null;
        var drivers = Drivers(read, length);
        return drivers?.FirstOrDefault(d => address >= d.Base && address < d.Base + d.Size).Name;
    }

    // Not done on purpose: scanning the saved stack for driver addresses (BlueScreenView-style). On the owner's real 0x119
    // (graphics) dump it found WdFilter.sys and appid.sys, which would wrongly blame Defender. Only the bugcheck's own
    // address parameter is trusted.

    public sealed record Driver(string Name, ulong Base, uint Size);

    /// <summary>The triage dump's driver list, or null if this isn't a small dump or the layout doesn't check out.</summary>
    public static List<Driver>? Drivers(ReadAt read, long length)
    {
        var t = read(Header64Size, 0x48);
        if (t.Length < 0x48) return null;
        var listOffset = BitConverter.ToUInt32(t, 48);
        var count = BitConverter.ToUInt32(t, 52);
        var poolOffset = BitConverter.ToUInt32(t, 56);
        if (count is 0 or > 4096 || listOffset < Header64Size || listOffset >= length || poolOffset <= listOffset || poolOffset >= length) return null;
        // DUMP_DRIVER_ENTRY64 = name offset + padding + KLDR_DATA_TABLE_ENTRY64; its size isn't documented, so derive
        // the stride from the gap to the string pool and confirm it by checking that every entry decodes cleanly.
        var span = poolOffset - listOffset;
        foreach (var stride in Strides(span, count))
        {
            var table = read(listOffset, (int)Math.Min(span, stride * count));
            if (table.Length < stride * count) continue;
            var list = new List<Driver>();
            for (var i = 0; i < count; i++)
            {
                var e = i * stride;
                var nameOffset = BitConverter.ToUInt32(table, (int)e);
                var dllBase = BitConverter.ToUInt64(table, (int)e + 8 + 0x30);
                var size = BitConverter.ToUInt32(table, (int)e + 8 + 0x40);
                if (dllBase < 0xFFFF800000000000UL || size is < 0x1000 or > 0x10000000 || ReadName(read, nameOffset, poolOffset, length) is not { } name) break;
                list.Add(new Driver(name, dllBase, size));
            }
            if (list.Count == count) return list;
        }
        return null;
    }

    private static IEnumerable<uint> Strides(uint span, uint count)
    {
        var exact = span / count;
        if (exact % 8 == 0) yield return exact;
        for (uint s = 0x80; s <= 0x140; s += 8)
            if (s != exact && s * count <= span) yield return s;
    }

    /// <summary>A DUMP_STRING: a 32-bit length in characters, then UTF-16 text. Must look like a module file name.</summary>
    private static string? ReadName(ReadAt read, uint offset, uint poolOffset, long length)
    {
        if (offset < poolOffset || offset + 4 >= length) return null;
        var head = read(offset, 4);
        if (head.Length < 4) return null;
        var chars = BitConverter.ToUInt32(head, 0);
        if (chars is 0 or > 260) return null;
        var bytes = read(offset + 4, (int)chars * 2);
        if (bytes.Length < chars * 2) return null;
        var name = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E)) return null;
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot >= name.Length - 4 ? name : null;
    }
}
