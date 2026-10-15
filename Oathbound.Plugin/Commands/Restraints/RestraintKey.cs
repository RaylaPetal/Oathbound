using System;
using System.Security.Cryptography;
using System.Text;

namespace Oathbound.Plugin.Commands;

/// A restraint lock's password travels and is stored only as `salt.hash`, so it never shows in the Sub's chat. Not a
/// security boundary: a short password can be worked out from the Sub's own config.
public static class RestraintKey
{
    public const int MinLength = 4;
    public const int MaxLength = 20;
    public static readonly TimeSpan WrongGuessWait = TimeSpan.FromSeconds(30);

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int SaltLength = 4;
    private const int HashLength = 13;

    public static bool IsValidPassword(string? password) =>
        password?.Trim().Length is >= MinLength and <= MaxLength;

    public static string ToWire(string password)
    {
        var salt = new char[SaltLength];
        for (var i = 0; i < SaltLength; i++)
            salt[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var saltText = new string(salt);
        return $"{saltText}.{Hash(saltText, password)}";
    }

    public static bool IsValidWire(string wire)
    {
        var dot = wire.IndexOf('.');
        return dot == SaltLength && wire.Length == SaltLength + 1 + HashLength
            && wire.Replace(".", "").ToUpperInvariant().AsSpan().IndexOfAnyExcept(Alphabet) < 0;
    }

    public static bool Matches(string wire, string guess)
    {
        if (!IsValidWire(wire))
            return false;
        var salt = wire[..SaltLength];
        var expected = Encoding.ASCII.GetBytes(wire[(SaltLength + 1)..].ToUpperInvariant());
        var actual = Encoding.ASCII.GetBytes(Hash(salt, guess));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Hash(string salt, string password)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt.ToUpperInvariant()}\n{password.Trim().ToLowerInvariant()}"));
        var sb = new StringBuilder(HashLength);
        var buffer = 0;
        var bits = 0;
        foreach (var b in digest)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && sb.Length < HashLength)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
            if (sb.Length == HashLength)
                break;
        }
        return sb.ToString();
    }
}
