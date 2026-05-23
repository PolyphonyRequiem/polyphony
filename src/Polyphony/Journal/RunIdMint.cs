using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Polyphony.Journal;

/// <summary>
/// Mints ULID-shaped run ids without taking a dependency on an external
/// ULID package. Produces 26-character Crockford-base32 strings of the
/// canonical shape <c>{timestamp:10}{random:16}</c> per the ULID spec
/// (<see href="https://github.com/ulid/spec"/>): 48-bit big-endian
/// millisecond timestamp followed by 80 bits of cryptographic randomness.
///
/// AOT-safe — no reflection, no allocations beyond the returned string.
/// </summary>
public static class RunIdMint
{
    /// <summary>The canonical ULID length in characters.</summary>
    public const int UlidLength = 26;

    // Crockford base32 alphabet — excludes I, L, O, U to dodge visual
    // ambiguity. Order is part of the ULID spec.
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Returns a fresh ULID-shaped run id seeded from the current UTC
    /// timestamp and <see cref="RandomNumberGenerator"/>.
    /// </summary>
    public static string NewRunId() => NewRunId(DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns a fresh ULID-shaped run id seeded from the supplied
    /// timestamp. The 80 random bits still come from
    /// <see cref="RandomNumberGenerator"/>.
    /// </summary>
    public static string NewRunId(DateTimeOffset timestamp)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        return Compose(timestamp, random);
    }

    internal static string Compose(DateTimeOffset timestamp, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length != 10)
        {
            throw new ArgumentException("ULID randomness must be exactly 10 bytes (80 bits).", nameof(randomness));
        }

        var unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp predates the Unix epoch; ULID time component is unsigned.");
        }

        // ULID byte layout: 6 bytes timestamp (big-endian, top bits zeroed) + 10 bytes random.
        Span<byte> bytes = stackalloc byte[16];
        Span<byte> tsScratch = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tsScratch, (ulong)unixMs);
        tsScratch[2..8].CopyTo(bytes);
        randomness.CopyTo(bytes[6..]);

        return EncodeCrockford(bytes);
    }

    /// <summary>
    /// True when <paramref name="value"/> is a 26-character Crockford-base32
    /// string. Useful for validating values arriving from the launcher or
    /// from a manifest field.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != UlidLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (CrockfordAlphabet.IndexOf(char.ToUpperInvariant(c)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string EncodeCrockford(ReadOnlySpan<byte> source)
    {
        // 128 bits / 5 bits per char = 25.6 → ULID uses 26 chars, with the
        // top 2 bits of the first base32 symbol fixed at 0. We compute by
        // packing into a single 128-bit value carried as two ulongs.
        var hi = BinaryPrimitives.ReadUInt64BigEndian(source[..8]);
        var lo = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);

        Span<char> result = stackalloc char[UlidLength];

        // Emit from the LSB upward, then reverse.
        for (var i = UlidLength - 1; i >= 0; i--)
        {
            var index = (int)(lo & 0x1F);
            result[i] = CrockfordAlphabet[index];

            // Shift the 128-bit value right by 5 bits.
            lo = (lo >> 5) | (hi << 59);
            hi >>= 5;
        }

        return new string(result);
    }
}
