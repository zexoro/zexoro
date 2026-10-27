using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Zexoro.Signing.Tests;

/// <summary>
/// Tests against throwaway keys in <c>OpenPgpKeys/</c>, made with gpg 2.4 under
/// <c>--faked-system-time 20260101T000000!</c> (every key was created on 2026-01-01 00:00 UTC) and
/// exported with <c>gpg --export-secret-keys --armor</c>:
/// <c>rsa2048</c> (RSA 2048 sign+certify primary and an encryption subkey, unprotected),
/// <c>rsa3072-protected</c> (gpg's default protection, AES-128 with an iterated and salted SHA-1
/// S2K, password <c>test-password</c>), <c>rsa2048-aes256</c> (AES-256 with SHA-512, password
/// <c>pässwörd</c>), <c>rsa2048-subkey</c> (certify-only primary with an RSA signing subkey),
/// <c>rsa2048-expired</c> (expired 2026-01-02 12:00 UTC) and <c>ed25519</c>. <c>*.pub.asc</c> are
/// the public keys, <c>data.rsa2048.sig</c> gpg's detached SHA-256 signature of <c>data.bin</c>
/// made with <c>rsa2048</c>.
/// </summary>
public sealed class OpenPgpSigningKeyTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "OpenPgpKeys", name));

    private static OpenPgpSigningKey Load(string name, string? password = null) => OpenPgpSigningKey.Load(Fixture(name), password);

    [Fact]
    public void Load_UnprotectedRsaKey_ReadsThePrimaryKey()
    {
        using var key = Load("rsa2048.sec.asc");

        Assert.Equal("54A7CAC5290E21258177E69BEA5D0F7E8BD10333", key.FingerprintHex);
        Assert.Equal("EA5D0F7E8BD10333", key.KeyId);
        Assert.Equal(2048, key.KeySize);
        Assert.Equal(Created, key.CreationTime);
        Assert.Null(key.ExpirationTime);
        Assert.False(key.IsSubkey);
        Assert.Equal("zexoro test rsa <rsa@zexoro.invalid>", key.UserId);
    }

    [Fact]
    public void Load_BinaryExport_ReadsTheKey()
    {
        var binary = OpenPgpPackets.Dearmor(Encoding.ASCII.GetString(Fixture("rsa2048.sec.asc")), "PGP PRIVATE KEY BLOCK");

        using var key = OpenPgpSigningKey.Load(binary, null);

        Assert.Equal("54A7CAC5290E21258177E69BEA5D0F7E8BD10333", key.FingerprintHex);
    }

    [Fact]
    public void Load_ProtectedKeyWithPassword_DecryptsIt()
    {
        using var key = Load("rsa3072-protected.sec.asc", "test-password");

        Assert.Equal("AB5C381C60FBD7081B5546B299DD37E4BE490FE7", key.FingerprintHex);
        Assert.Equal(3072, key.KeySize);
        AssertVerifies(key, "rsa3072-protected.pub.asc");
    }

    [Fact]
    public void Load_Aes256Sha512KeyWithUnicodePassword_DecryptsIt()
    {
        using var key = Load("rsa2048-aes256.sec.asc", "pässwörd");

        Assert.Equal("B723752A64EBA5C2A5EB084B2AB42D920E9D30E9", key.FingerprintHex);
    }

    [Fact]
    public void Load_WrongPassword_Throws()
    {
        var ex = Assert.Throws<SigningException>(() => Load("rsa3072-protected.sec.asc", "nope"));

        Assert.Contains("password", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wrong", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ProtectedKeyWithoutPassword_Throws()
    {
        var ex = Assert.Throws<SigningException>(() => Load("rsa3072-protected.sec.asc"));

        Assert.Contains("protected by a passphrase", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CertifyOnlyPrimary_UsesTheSigningSubkey()
    {
        using var key = Load("rsa2048-subkey.sec.asc");

        Assert.True(key.IsSubkey);
        Assert.Equal("70A2A8573F6B88C60BBD8AD5C3B70B53A7B82935", key.FingerprintHex);
        AssertVerifies(key, "rsa2048-subkey.pub.asc");
    }

    [Fact]
    public void Load_Ed25519Key_ThrowsWithAnRsaHint()
    {
        var ex = Assert.Throws<SigningException>(() => Load("ed25519.sec.asc"));

        Assert.Contains("Ed25519", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rsa4096", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_PublicKey_Throws()
    {
        var ex = Assert.Throws<SigningException>(() => Load("rsa2048.pub.asc"));

        Assert.Contains("--export-secret-keys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ChangedArmor_ThrowsChecksumError()
    {
        var text = Encoding.ASCII.GetString(Fixture("rsa2048.sec.asc"));
        var lines = text.Split('\n');
        var index = Array.FindIndex(lines, l => l.Length == 64);
        var line = lines[index].ToCharArray();
        line[10] = line[10] == 'A' ? 'B' : 'A';
        lines[index] = new string(line);

        var ex = Assert.Throws<SigningException>(() => OpenPgpSigningKey.Load(Encoding.ASCII.GetBytes(string.Join('\n', lines)), null));

        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NotAKey_Throws()
    {
        Assert.Throws<SigningException>(() => OpenPgpSigningKey.Load("hello"u8, null));
    }

    [Fact]
    public void ExpirationTime_ExpiredKey_IsReadFromTheSelfSignature()
    {
        using var key = Load("rsa2048-expired.sec.asc");

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), key.ExpirationTime);
        var ex = Assert.Throws<SigningException>(() => key.SignBinary("x"u8, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Contains("expired", ex.Message, StringComparison.Ordinal);
        key.SignBinary("x"u8, Created.AddHours(1));
    }

    [Fact]
    public void SignBinary_BeforeTheKeyWasCreated_Throws()
    {
        using var key = Load("rsa2048.sec.asc");

        var ex = Assert.Throws<SigningException>(() => key.SignBinary("x"u8, Created.AddSeconds(-1)));

        Assert.Contains("before", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignBinary_SameKeyAsGpg_HasGpgsStructureAndVerifiesLikeGpgsSignature()
    {
        var gpg = Assert.Single(OpenPgpPackets.Read(Fixture("data.rsa2048.sig"))).Body.ToArray();
        var data = Fixture("data.bin");
        using var key = Load("rsa2048.sec.asc");

        // The verifier below accepts gpg's own signature, so it checks what an OpenPGP verifier checks.
        Assert.True(Verifies(gpg, data, "rsa2048.pub.asc", key.Fingerprint));
        var ours = OpenPgpPackets.Read(key.SignBinary(data, SignatureCreationTime(gpg))).Single().Body.ToArray();
        Assert.True(Verifies(ours, data, "rsa2048.pub.asc", key.Fingerprint));
        Assert.False(Verifies(ours, "other data"u8.ToArray(), "rsa2048.pub.asc", key.Fingerprint));

        // Same header fields, issuer fingerprint first and creation time, issuer key ID unhashed;
        // gpg also adds the signer's user ID when one is named with --local-user.
        Assert.Equal(gpg.AsSpan(0, 4).ToArray(), ours.AsSpan(0, 4).ToArray());
        Assert.Equal(gpg.AsSpan(6, 29).ToArray(), ours.AsSpan(6, 29).ToArray());
        Assert.Equal([0, 10, 9, 16, .. key.Fingerprint.AsSpan(12)], ours.AsSpan(35, 12).ToArray());
    }

    [Fact]
    public void SignBinary_PacketStructure_IsVersion4RsaSha256()
    {
        using var key = Load("rsa2048.sec.asc");
        var time = Created.AddDays(30);

        var packet = Assert.Single(OpenPgpPackets.Read(key.SignBinary("data"u8, time)));

        Assert.Equal(OpenPgpPackets.SignatureTag, packet.Tag);
        var body = packet.Body.Span;
        Assert.Equal(4, body[0]);
        Assert.Equal(0x00, body[1]);
        Assert.Equal(1, body[2]);
        Assert.Equal(8, body[3]);
        Assert.Equal(time, SignatureCreationTime(body));
        AssertVerifies(key, "rsa2048.pub.asc");
    }

    [Fact]
    public void DeriveKey_IteratedAndSalted_FollowsRfc9580()
    {
        // 32 bytes from SHA-1 need two hash contexts; the second is preloaded with one zero byte.
        var salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var password = "pw"u8.ToArray();
        var input = salt.Concat(password).ToArray();
        var stream = Enumerable.Range(0, 100).SelectMany(_ => input).Take(1000).ToArray();
#pragma warning disable CA5350 // the S2K under test is defined with SHA-1
        var expected = SHA1.HashData(stream).Concat(SHA1.HashData(new byte[] { 0 }.Concat(stream).ToArray())).Take(32).ToArray();
#pragma warning restore CA5350

        var key = OpenPgpSigningKey.DeriveKey(password, salt, 1000, HashAlgorithmName.SHA1, 32);

        Assert.Equal(expected, key);
    }

    private static DateTimeOffset SignatureCreationTime(ReadOnlySpan<byte> body)
    {
        var hashedLength = BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
        var hashed = body.Slice(6, hashedLength);
        for (var i = 0; i < hashed.Length;)
        {
            var length = hashed[i];
            if (hashed[i + 1] == 2)
            {
                return DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(hashed[(i + 2)..]));
            }

            i += 1 + length;
        }

        throw new InvalidOperationException("no creation time");
    }

    /// <summary>Verifies a fresh signature with the RSA key of the matching public key export.</summary>
    private static void AssertVerifies(OpenPgpSigningKey key, string publicKeyFile)
    {
        var data = "verify me"u8.ToArray();
        var packet = OpenPgpPackets.Read(key.SignBinary(data, Created.AddDays(1)))[0].Body.ToArray();
        Assert.True(Verifies(packet, data, publicKeyFile, key.Fingerprint));
    }

    /// <summary>Verifies a version 4 RSA/SHA-256 signature packet body over <paramref name="data"/>, as an OpenPGP verifier would.</summary>
    private static bool Verifies(byte[] packet, byte[] data, string publicKeyFile, byte[] fingerprint)
    {
        var hashedLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        var hashedEnd = 6 + hashedLength;
        var unhashedLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(hashedEnd));
        var position = hashedEnd + 2 + unhashedLength + 2;
        var signature = OpenPgpPackets.ReadMpi(packet, ref position);
        var trailer = new byte[] { 4, 0xFF, 0, 0, (byte)(hashedEnd >> 8), (byte)hashedEnd };
        var digest = SHA256.HashData([.. data, .. packet.AsSpan(0, hashedEnd), .. trailer]);
        if (digest[0] != packet[hashedEnd + 2 + unhashedLength] || digest[1] != packet[hashedEnd + 3 + unhashedLength])
        {
            return false;
        }

        var publicPackets = OpenPgpPackets.Read(OpenPgpPackets.Dearmor(Encoding.ASCII.GetString(Fixture(publicKeyFile)), "PGP PUBLIC KEY BLOCK"));
        var match = publicPackets
            .Where(p => p.Tag is 6 or 14)
            .Select(p => p.Body.ToArray())
            .Single(b => Fingerprint(b).SequenceEqual(fingerprint));
        var at = 6;
        using var rsa = RSA.Create(new RSAParameters { Modulus = OpenPgpPackets.ReadMpi(match, ref at), Exponent = OpenPgpPackets.ReadMpi(match, ref at) });
        var padded = new byte[rsa.KeySize / 8];
        signature.CopyTo(padded, padded.Length - signature.Length);
        return rsa.VerifyHash(digest, padded, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static byte[] Fingerprint(byte[] publicKeyBody)
    {
        var at = 6;
        OpenPgpPackets.ReadMpi(publicKeyBody, ref at);
        OpenPgpPackets.ReadMpi(publicKeyBody, ref at);
#pragma warning disable CA5350 // version 4 fingerprints are SHA-1
        return SHA1.HashData([0x99, (byte)(at >> 8), (byte)at, .. publicKeyBody.AsSpan(0, at)]);
#pragma warning restore CA5350
    }
}
