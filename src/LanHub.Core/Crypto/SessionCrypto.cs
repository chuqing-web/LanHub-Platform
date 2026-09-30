using System.Security.Cryptography;
using System.Text;

namespace LanHub.Core.Crypto;

public static class SessionCrypto
{
    public static byte[] CreateSalt(int size = 16)
    {
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public static string CreateRoomCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var n = BitConverter.ToUInt32(bytes) % 1_000_000u;
        return n.ToString("D6");
    }

    public static byte[] ComputeJoinProof(string roomCode, byte[] salt, byte[] nonce)
    {
        var key = Encoding.UTF8.GetBytes(roomCode);
        var data = new byte[salt.Length + nonce.Length];
        Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, data, salt.Length, nonce.Length);
        return HMACSHA256.HashData(key, data);
    }

    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static byte[] DeriveSessionKey(string roomCode, byte[] salt)
    {
        var ikm = Encoding.UTF8.GetBytes(roomCode);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            outputLength: 32,
            salt: salt,
            info: "LanHub.v1"u8.ToArray());
    }

    public static byte[] Seal(byte[] key, byte[] plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return result;
    }

    public static byte[] Open(byte[] key, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 12 + 16)
            throw new CryptographicException("Sealed payload too short.");

        var nonce = sealedBytes.AsSpan(0, 12);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - 16, 16);
        var ciphertext = sealedBytes.AsSpan(12, sealedBytes.Length - 12 - 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static string ToBase64(byte[] data) => Convert.ToBase64String(data);
    public static byte[] FromBase64(string data) => Convert.FromBase64String(data);
}
