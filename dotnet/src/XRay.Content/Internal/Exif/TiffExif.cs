using System.Globalization;
using System.Text;

namespace XRay.Content.Internal.Exif;

/// <summary>Which IFD an entry was read from; the GPS tag numbers overlap the main ones.</summary>
internal enum ExifIfd { Main, Gps }

/// <summary>
/// A bounds-checked reader for the TIFF structure EXIF is stored in (the
/// APP1/eXIf/EXIF payload for the others).
/// </summary>
/// <remarks>
/// Replaces the EXIF profile reader of the imaging library the package used to depend on, and keeps
/// its output: values that fit in the entry's four bytes are read first, then the ones stored
/// elsewhere, and a value prints as its elements joined by a space, a rational as <c>n/d</c> (just
/// <c>n</c> when <c>d</c> is 1), a string up to its first NUL. Everything read from the block is
/// untrusted: an entry whose data lies outside it is skipped, IFD chains cannot loop, and nothing
/// but a short or malformed block can make this return fewer entries.
/// </remarks>
internal static class TiffExif
{
    private const int MaxIfds = 64;
    private const int MaxEntriesPerIfd = 4096;

    private static readonly byte[] ExifPrefix = "Exif\0\0"u8.ToArray();

    internal readonly record struct Value(ushort Tag, ExifIfd Ifd, string Text);

    /// <summary>The block as bytes starting at the TIFF header, or empty when it has none.</summary>
    internal static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> block) =>
        block.StartsWith(ExifPrefix) ? block[ExifPrefix.Length..] : block;

    internal static bool HasHeader(ReadOnlySpan<byte> tiff) =>
        tiff.Length >= 8
        && ((tiff[0] == (byte)'I' && tiff[1] == (byte)'I' && tiff[2] == 42 && tiff[3] == 0)
            || (tiff[0] == (byte)'M' && tiff[1] == (byte)'M' && tiff[2] == 0 && tiff[3] == 42));

    public static List<Value> Read(ReadOnlySpan<byte> block)
    {
        var values = new List<Value>();
        var tiff = Unwrap(block);
        if (!HasHeader(tiff)) return values;

        bool le = tiff[0] == (byte)'I';
        var inline = new List<Entry>();
        var deferred = new List<Entry>();
        var seen = new HashSet<uint>();
        int budget = MaxIfds;

        uint ifd0 = U32(tiff, 4, le);
        ReadIfd(tiff, ifd0, le, ExifIfd.Main, seen, ref budget, inline, deferred, out uint exifPointer, out uint gpsPointer);
        if (exifPointer != 0) ReadIfd(tiff, exifPointer, le, ExifIfd.Main, seen, ref budget, inline, deferred, out _, out _);
        if (gpsPointer != 0) ReadIfd(tiff, gpsPointer, le, ExifIfd.Gps, seen, ref budget, inline, deferred, out _, out _);

        foreach (var entry in inline) Add(tiff, entry, le, values);
        foreach (var entry in deferred) Add(tiff, entry, le, values);
        return values;
    }

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, int DataOffset, ExifIfd Ifd);

    /// <summary>Reads one IFD's entries into the two lists. The chain of following IFDs (the thumbnail's) is not read.</summary>
    private static void ReadIfd(
        ReadOnlySpan<byte> tiff, uint offset, bool le, ExifIfd kind, HashSet<uint> seen, ref int budget,
        List<Entry> inline, List<Entry> deferred, out uint exifPointer, out uint gpsPointer)
    {
        exifPointer = 0;
        gpsPointer = 0;
        if (budget-- <= 0 || offset < 8 || offset > tiff.Length - 2 || !seen.Add(offset)) return;

        int count = U16(tiff, (int)offset, le);
        if (count > MaxEntriesPerIfd) count = MaxEntriesPerIfd;
        long entries = offset + 2;
        if (entries + (long)count * 12 + 4 > tiff.Length)
            count = (int)Math.Max(0, (tiff.Length - entries - 4) / 12);

        for (int i = 0; i < count; i++)
        {
            int at = (int)(entries + i * 12L);
            ushort tag = U16(tiff, at, le);
            ushort type = U16(tiff, at + 2, le);
            uint n = U32(tiff, at + 4, le);

            if (kind == ExifIfd.Main && type is 3 or 4 && n == 1)
            {
                if (tag == 0x8769) exifPointer = type == 3 ? U16(tiff, at + 8, le) : U32(tiff, at + 8, le);
                else if (tag == 0x8825) gpsPointer = type == 3 ? U16(tiff, at + 8, le) : U32(tiff, at + 8, le);
            }

            int unit = TypeSize(type);
            if (unit == 0) continue;
            ulong total = (ulong)unit * n;
            if (total <= 4)
            {
                inline.Add(new Entry(tag, type, n, at + 8, kind));
            }
            else
            {
                uint data = U32(tiff, at + 8, le);
                if (data > (uint)tiff.Length || total > (ulong)(tiff.Length - data)) continue;
                deferred.Add(new Entry(tag, type, n, (int)data, kind));
            }
        }

    }

    private static void Add(ReadOnlySpan<byte> tiff, Entry e, bool le, List<Value> into)
    {
        string? text = Format(tiff, e, le);
        if (!string.IsNullOrEmpty(text)) into.Add(new Value(e.Tag, e.Ifd, text));
    }

    private static string? Format(ReadOnlySpan<byte> tiff, Entry e, bool le)
    {
        int n = (int)e.Count;
        int at = e.DataOffset;

        if (e.Type == 2)
        {
            var raw = tiff.Slice(at, n);
            int nul = raw.IndexOf((byte)0);
            if (nul >= 0) raw = raw[..nul];
            return Encoding.ASCII.GetString(raw);
        }

        var parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            parts[i] = e.Type switch
            {
                1 or 7 => tiff[at + i].ToString(CultureInfo.InvariantCulture),
                6 => ((sbyte)tiff[at + i]).ToString(CultureInfo.InvariantCulture),
                3 => U16(tiff, at + i * 2, le).ToString(CultureInfo.InvariantCulture),
                8 => ((short)U16(tiff, at + i * 2, le)).ToString(CultureInfo.InvariantCulture),
                4 => U32(tiff, at + i * 4, le).ToString(CultureInfo.InvariantCulture),
                9 => ((int)U32(tiff, at + i * 4, le)).ToString(CultureInfo.InvariantCulture),
                5 => Fraction(U32(tiff, at + i * 8, le), U32(tiff, at + i * 8 + 4, le)),
                10 => Fraction((int)U32(tiff, at + i * 8, le), (int)U32(tiff, at + i * 8 + 4, le)),
                11 => BitConverter.Int32BitsToSingle((int)U32(tiff, at + i * 4, le)).ToString(CultureInfo.InvariantCulture),
                12 => BitConverter.Int64BitsToDouble((long)U64(tiff, at + i * 8, le)).ToString(CultureInfo.InvariantCulture),
                _ => "",
            };
        }
        return string.Join(' ', parts);
    }

    private static string Fraction(long numerator, long denominator) =>
        denominator == 1
            ? numerator.ToString(CultureInfo.InvariantCulture)
            : $"{numerator.ToString(CultureInfo.InvariantCulture)}/{denominator.ToString(CultureInfo.InvariantCulture)}";

    internal static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    internal static ushort U16(ReadOnlySpan<byte> b, int at, bool le) =>
        (uint)at + 2 > (uint)b.Length ? (ushort)0
        : le ? (ushort)(b[at] | b[at + 1] << 8) : (ushort)(b[at] << 8 | b[at + 1]);

    internal static uint U32(ReadOnlySpan<byte> b, int at, bool le) =>
        (uint)at + 4 > (uint)b.Length ? 0
        : le ? (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24)
             : (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static ulong U64(ReadOnlySpan<byte> b, int at, bool le) =>
        le ? U32(b, at, true) | (ulong)U32(b, at + 4, true) << 32
           : (ulong)U32(b, at, false) << 32 | U32(b, at + 4, false);
}
