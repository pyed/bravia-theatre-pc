using System;
using System.Security.Cryptography;
using System.Text;

namespace BraviaTheatre.Core.Wire;

/// <summary>
/// Cryptographic operations (HMAC-SHA256) for Sony Seeds authentication.
/// </summary>
public static class PacketSigner
{
    public static byte[] ParseHmacKey(string hmacKeyHex)
    {
        if (hmacKeyHex.Length == 64)
        {
            return Convert.FromHexString(hmacKeyHex);
        }

        var keyBytes = new byte[32];
        var utf8 = Encoding.UTF8.GetBytes(hmacKeyHex);
        Buffer.BlockCopy(utf8, 0, keyBytes, 0, Math.Min(utf8.Length, 32));
        return keyBytes;
    }

    public static byte[] ComputeHmac(byte[] keyBytes, byte[] message)
    {
        using var hmac = new HMACSHA256(keyBytes);
        return hmac.ComputeHash(message);
    }

    public static byte[] ComputeHmac(string hmacKeyHex, byte[] message)
    {
        var key = ParseHmacKey(hmacKeyHex);
        return ComputeHmac(key, message);
    }
}
