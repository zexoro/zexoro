using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Zexoro.Signing;

/// <summary>
/// An RSA signing key read from an OpenPGP secret key export (<c>gpg --export-secret-keys
/// --armor</c>; RFC 9580), which makes OpenPGP v4 signatures with SHA-256, as rpm verifies them
/// (docs/design/0006-desktop-formats.md section 3).
/// </summary>
/// <remarks>
/// <para>
/// Supported: version 4 keys with RSA (algorithm 1 or 3), unprotected or protected with a
/// passphrase through an iterated and salted, salted or simple S2K and AES-128, -192 or -256 in
/// CFB mode with a SHA-1 or two-byte checksum, as gpg exports them. The primary key signs when its
/// key flags allow signing; otherwise the first RSA subkey that may sign does. Old rpm versions
/// only verify signatures made by the primary key, so a primary key that can sign is preferred.
/// </para>
/// <para>
/// Not supported, with a clear error: Ed25519 (EdDSA), ECDSA and DSA keys (the base class library
/// has no Ed25519; rpm accepts RSA everywhere), version 5 and 6 keys, AEAD-protected keys,
/// secret key stubs (smartcards, <c>--export-secret-subkeys</c> without the subkey), keys shorter
/// than 2048 bits (rejected by the policies of current rpm builds) and keys that expired or are
/// revoked at the signature time. Self-signatures are read for key flags, expiry and revocation
/// but not verified: the key is the user's own.
/// </para>
/// </remarks>
public sealed class OpenPgpSigningKey : IDisposable
{
    private const int RsaAlgorithm = 1;
    private const int RsaSignOnlyAlgorithm = 3;
    private const int Sha256Algorithm = 8;
    private const byte SignFlag = 0x02;

    private readonly RSA _rsa;
    private readonly int _algorithm;

    private OpenPgpSigningKey(RSA rsa, int algorithm, byte[] fingerprint, DateTimeOffset created, DateTimeOffset? expires, string? userId, bool isSubkey)
    {
        _rsa = rsa;
        _algorithm = algorithm;
        Fingerprint = fingerprint;
        CreationTime = created;
        ExpirationTime = expires;
        UserId = userId;
        IsSubkey = isSubkey;
    }

    /// <summary>The 20-byte version 4 fingerprint of the key that signs.</summary>
    public byte[] Fingerprint { get; }

    /// <summary>The fingerprint as upper-case hex, as gpg prints it.</summary>
    public string FingerprintHex => Convert.ToHexString(Fingerprint);

    /// <summary>The key ID: the last 8 bytes of the fingerprint, as upper-case hex.</summary>
    public string KeyId => Convert.ToHexString(Fingerprint, 12, 8);

    /// <summary>When the signing key was created.</summary>
    public DateTimeOffset CreationTime { get; }

    /// <summary>When the signing key expires, or null when it does not.</summary>
    public DateTimeOffset? ExpirationTime { get; }

    /// <summary>The first user ID of the key, or null when it has none.</summary>
    public string? UserId { get; }

    /// <summary>Whether a subkey signs, because the primary key may not.</summary>
    public bool IsSubkey { get; }

    /// <summary>The RSA modulus size in bits.</summary>
    public int KeySize => _rsa.KeySize;

    /// <summary>A one-line description for messages, such as <c>RSA 4096 key 1BF4… (Name &lt;mail&gt;)</c>.</summary>
    public string Description =>
        string.Create(CultureInfo.InvariantCulture, $"RSA {KeySize} {(IsSubkey ? "subkey" : "key")} {FingerprintHex}{(UserId is null ? string.Empty : $" ({UserId})")}");

    public void Dispose() => _rsa.Dispose();

    /// <summary>
    /// Reads the signing key from an exported OpenPGP secret key: ASCII-armored
    /// (<c>-----BEGIN PGP PRIVATE KEY BLOCK-----</c>) or binary.
    /// </summary>
    /// <param name="key">The export.</param>
    /// <param name="passphrase">The passphrase of a protected key, or null.</param>
    /// <exception cref="SigningException">The key cannot be read or used; the message says why.</exception>
    public static OpenPgpSigningKey Load(ReadOnlySpan<byte> key, string? passphrase)
    {
        byte[] binary;
        var text = Encoding.UTF8.GetString(key);
        if (text.Contains("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal))
        {
            throw new SigningException("The OpenPGP key is a public key; export the secret key with 'gpg --export-secret-keys --armor <key ID>'.");
        }

        if (text.Contains("-----BEGIN PGP", StringComparison.Ordinal))
        {
            try
            {
                binary = OpenPgpPackets.Dearmor(text, "PGP PRIVATE KEY BLOCK");
            }
            catch (FormatException ex)
            {
                throw new SigningException($"The OpenPGP secret key cannot be read: {ex.Message}.", ex);
            }
        }
        else
        {
            binary = key.ToArray();
        }

        try
        {
            return Load(OpenPgpPackets.Read(binary), passphrase);
        }
        catch (FormatException ex)
        {
            throw new SigningException($"The OpenPGP secret key cannot be read: {ex.Message}.", ex);
        }
    }

    /// <summary>
    /// Signs <paramref name="data"/> as a binary document (signature type 0x00) with SHA-256 and
    /// returns the version 4 signature packet. The hashed area holds the issuer fingerprint and the
    /// creation time, the unhashed area the issuer key ID, in the order gpg writes them. RSA
    /// PKCS #1 v1.5 signatures are deterministic, so the packet depends only on the key, the data
    /// and <paramref name="time"/>.
    /// </summary>
    /// <param name="data">The signed data.</param>
    /// <param name="time">The signature creation time (whole seconds are used).</param>
    /// <exception cref="SigningException">The time lies outside the key's validity.</exception>
    public byte[] SignBinary(ReadOnlySpan<byte> data, DateTimeOffset time)
    {
        CheckValidAt(time);
        var seconds = time.ToUnixTimeSeconds();
        var hashed = new List<byte> { 22, 33, 4 };
        hashed.AddRange(Fingerprint);
        hashed.AddRange([5, 2, (byte)(seconds >> 24), (byte)(seconds >> 16), (byte)(seconds >> 8), (byte)seconds]);
        var prefix = new List<byte> { 4, 0x00, (byte)_algorithm, Sha256Algorithm, (byte)(hashed.Count >> 8), (byte)hashed.Count };
        prefix.AddRange(hashed);
        var trailer = new byte[6];
        trailer[0] = 4;
        trailer[1] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(trailer.AsSpan(2), (uint)prefix.Count);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(data);
        hash.AppendData(prefix.ToArray());
        hash.AppendData(trailer);
        var digest = hash.GetHashAndReset();
        var signature = _rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var body = new List<byte>(prefix);
        body.AddRange([0, 10, 9, 16]);
        body.AddRange(Fingerprint.AsSpan(12, 8).ToArray());
        body.Add(digest[0]);
        body.Add(digest[1]);
        OpenPgpPackets.WriteMpi(body, signature);

        // A legacy-format header with a two-byte length, as gpg and rpmsign write signatures.
        var packet = new byte[3 + body.Count];
        packet[0] = 0x80 | (OpenPgpPackets.SignatureTag << 2) | 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)body.Count);
        body.CopyTo(packet, 3);
        return packet;
    }

    /// <summary>Whether a signature made at <paramref name="time"/> is within the key's validity.</summary>
    /// <exception cref="SigningException">It is not.</exception>
    public void CheckValidAt(DateTimeOffset time)
    {
        if (time < CreationTime)
        {
            throw new SigningException(
                $"The signature time {Format(time)} lies before the OpenPGP key {FingerprintHex} was created ({Format(CreationTime)}); verifiers reject such signatures.");
        }

        if (ExpirationTime is { } expires && time >= expires)
        {
            throw new SigningException(
                $"The OpenPGP key {FingerprintHex} expired on {Format(expires)}; extend it with 'gpg --quick-set-expire {FingerprintHex} <time>' and export it again.");
        }
    }

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>One key of the export (primary or subkey) with what its self-signatures say.</summary>
    private sealed class KeyEntry(OpenPgpPacket packet, bool isSubkey)
    {
        public OpenPgpPacket Packet { get; } = packet;

        public bool IsSubkey { get; } = isSubkey;

        public int Version { get; set; }

        public int Algorithm { get; set; }

        public DateTimeOffset Created { get; set; }

        public byte[] Fingerprint { get; set; } = [];

        /// <summary>Where the algorithm-specific public part ends in the packet body.</summary>
        public int PublicEnd { get; set; }

        public byte[][] PublicMpis { get; set; } = [];

        /// <summary>The newest self-signature's creation time, key flags and key expiration (seconds after creation).</summary>
        public DateTimeOffset? SelfSignatureTime { get; set; }

        public byte? Flags { get; set; }

        public long? ExpirationSeconds { get; set; }

        public bool Revoked { get; set; }

        public bool IsSecretKeyPacket => Packet.Tag is OpenPgpPackets.SecretKeyTag or OpenPgpPackets.SecretSubkeyTag;
    }

    private static OpenPgpSigningKey Load(List<OpenPgpPacket> packets, string? passphrase)
    {
        if (packets.Count == 0)
        {
            throw new SigningException("The OpenPGP secret key is empty.");
        }

        if (packets[0].Tag == OpenPgpPackets.PublicKeyTag)
        {
            throw new SigningException("The OpenPGP key is a public key; export the secret key with 'gpg --export-secret-keys --armor <key ID>'.");
        }

        if (packets[0].Tag != OpenPgpPackets.SecretKeyTag)
        {
            throw new SigningException("The data is not an OpenPGP secret key; export it with 'gpg --export-secret-keys --armor <key ID>'.");
        }

        var keys = new List<KeyEntry>();
        string? userId = null;
        foreach (var packet in packets)
        {
            switch (packet.Tag)
            {
                case OpenPgpPackets.SecretKeyTag when keys.Count == 0:
                    keys.Add(ReadPublicPart(new KeyEntry(packet, isSubkey: false)));
                    break;
                case OpenPgpPackets.SecretKeyTag or OpenPgpPackets.PublicKeyTag:
                    throw new SigningException("The OpenPGP export holds more than one key; export only the signing key with 'gpg --export-secret-keys --armor <key ID>'.");
                case OpenPgpPackets.SecretSubkeyTag or OpenPgpPackets.PublicSubkeyTag:
                    keys.Add(ReadPublicPart(new KeyEntry(packet, isSubkey: true)));
                    break;
                case OpenPgpPackets.UserIdTag:
                    userId ??= Encoding.UTF8.GetString(packet.Body.Span);
                    break;
                case OpenPgpPackets.SignatureTag:
                    ReadSelfSignature(packet.Body.Span, keys[0], keys[^1]);
                    break;
            }
        }

        var primary = keys[0];
        if (primary.Version != 4)
        {
            throw new SigningException(
                $"The OpenPGP key is a version {primary.Version} key; zexoro signs with version 4 RSA keys, which every rpm verifies. Create one with "
                + "'gpg --quick-gen-key \"Name <email>\" rsa4096 sign,cert never'.");
        }

        var signing = keys.FirstOrDefault(k => !k.IsSubkey && IsRsa(k) && MaySign(k) && Usable(k))
            ?? keys.FirstOrDefault(k => k.IsSubkey && k.Version == 4 && IsRsa(k) && MaySign(k) && Usable(k));
        if (signing is null)
        {
            if (keys.FirstOrDefault(k => !IsRsa(k) && MaySign(k)) is { } other)
            {
                throw new SigningException(
                    $"The OpenPGP key {Convert.ToHexString(other.Fingerprint)} is {AlgorithmName(other.Algorithm)}; zexoro signs rpm packages with RSA keys only, "
                    + "because .NET has no Ed25519 and RSA is verified by every rpm. Create an RSA key with "
                    + "'gpg --quick-gen-key \"Name <email>\" rsa4096 sign,cert never'.");
            }

            if (keys.Any(k => IsRsa(k) && MaySign(k) && !k.IsSecretKeyPacket))
            {
                throw new SigningException("The OpenPGP export has no secret part for its RSA signing key; export it with 'gpg --export-secret-keys --armor <key ID>'.");
            }

            throw new SigningException(keys.Any(k => IsRsa(k) && MaySign(k) && k.Revoked)
                ? "The OpenPGP key's RSA signing key is revoked."
                : "The OpenPGP key has no RSA key that may sign (key flag 'S').");
        }

        if (primary.Revoked)
        {
            throw new SigningException($"The OpenPGP key {Convert.ToHexString(primary.Fingerprint)} is revoked.");
        }

        DateTimeOffset? expires = signing.ExpirationSeconds is { } seconds and > 0 ? signing.Created.AddSeconds(seconds) : null;
        if (signing.IsSubkey && primary.ExpirationSeconds is { } primarySeconds and > 0)
        {
            var primaryExpires = primary.Created.AddSeconds(primarySeconds);
            expires = expires is { } own && own < primaryExpires ? own : primaryExpires;
        }

        var rsa = ReadRsaSecret(signing, passphrase);
        try
        {
            if (rsa.KeySize < 2048)
            {
                throw new SigningException(
                    $"The OpenPGP key {Convert.ToHexString(signing.Fingerprint)} has {rsa.KeySize.ToString(CultureInfo.InvariantCulture)} bits; rpm's verifiers require at least 2048.");
            }

            return new OpenPgpSigningKey(rsa, signing.Algorithm, signing.Fingerprint, signing.Created, expires, userId, signing.IsSubkey);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static bool IsRsa(KeyEntry key) => key.Algorithm is RsaAlgorithm or RsaSignOnlyAlgorithm;

    /// <summary>Key flags allow signing; without key flags, a primary key may sign (as gpg assumes).</summary>
    private static bool MaySign(KeyEntry key) => key.Flags is { } flags ? (flags & SignFlag) != 0 : !key.IsSubkey;

    private static bool Usable(KeyEntry key) => key.IsSecretKeyPacket && !key.Revoked;

    private static string AlgorithmName(int algorithm) => algorithm switch
    {
        17 => "a DSA key",
        18 => "an ECDH key",
        19 => "an ECDSA key",
        22 or 27 => "an Ed25519 (EdDSA) key",
        28 => "an Ed448 key",
        _ => $"a key of OpenPGP algorithm {algorithm.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>Reads version, creation time, algorithm and public MPIs, and computes the fingerprint.</summary>
    private static KeyEntry ReadPublicPart(KeyEntry key)
    {
        var body = key.Packet.Body.Span;
        if (body.Length < 6)
        {
            throw new FormatException("a key packet is truncated");
        }

        key.Version = body[0];
        if (key.Version != 4)
        {
            return key;
        }

        key.Created = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(body[1..]));
        key.Algorithm = body[5];
        var position = 6;
        if (IsRsa(key))
        {
            key.PublicMpis = [OpenPgpPackets.ReadMpi(body, ref position), OpenPgpPackets.ReadMpi(body, ref position)];
        }
        else
        {
            // Other algorithms' public parts are not needed: their fingerprint is still computed
            // from the whole public part, whose end is found by its structure.
            position = SkipPublicPart(body, key.Algorithm);
        }

        key.PublicEnd = position;

        // Version 4 fingerprint: SHA-1 over 0x99, a two-byte length and the public key packet body.
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData([0x99, (byte)(position >> 8), (byte)position]);
        hash.AppendData(body[..position]);
        key.Fingerprint = hash.GetHashAndReset();
        hash.Dispose();
        return key;
    }

    /// <summary>The end of the public part of a non-RSA version 4 key.</summary>
    private static int SkipPublicPart(ReadOnlySpan<byte> body, int algorithm)
    {
        var position = 6;
        void Mpis(ReadOnlySpan<byte> data, int count, ref int at)
        {
            for (var i = 0; i < count; i++)
            {
                OpenPgpPackets.ReadMpi(data, ref at);
            }
        }

        int Oid(ReadOnlySpan<byte> data, int at) => at < data.Length ? at + 1 + data[at] : throw new FormatException("a key packet is truncated");

        switch (algorithm)
        {
            case 16 or 20:
                Mpis(body, 3, ref position);
                break;
            case 17:
                Mpis(body, 4, ref position);
                break;
            case 19 or 22:
                position = Oid(body, position);
                Mpis(body, 1, ref position);
                break;
            case 18:
                position = Oid(body, position);
                Mpis(body, 1, ref position);
                position += 1 + body[position];
                break;
            case 25 or 27:
                position += 32;
                break;
            case 26:
                position += 56;
                break;
            case 28:
                position += 57;
                break;
            default:
                // Unknown algorithm: no secret part can be used anyway.
                return body.Length;
        }

        return Math.Min(position, body.Length);
    }

    /// <summary>
    /// Takes key flags, expiry and revocation from a signature that the primary key made over
    /// itself (a user ID certification or direct-key signature, a key revocation) or over the
    /// current subkey (binding, revocation). Third-party certifications are ignored.
    /// </summary>
    private static void ReadSelfSignature(ReadOnlySpan<byte> body, KeyEntry primary, KeyEntry current)
    {
        if (body.Length < 6 || body[0] != 4 || primary.Version != 4)
        {
            return;
        }

        var type = body[1];
        var hashedLength = BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
        if (6 + hashedLength + 2 > body.Length)
        {
            return;
        }

        var hashed = body.Slice(6, hashedLength);
        var unhashedLength = BinaryPrimitives.ReadUInt16BigEndian(body[(6 + hashedLength)..]);
        var unhashed = body.Slice(8 + hashedLength, Math.Min(unhashedLength, body.Length - 8 - hashedLength));
        DateTimeOffset? created = null;
        byte? flags = null;
        long? expiration = null;
        byte[]? issuer = null;
        foreach (var (subtype, data) in Subpackets(hashed))
        {
            switch (subtype)
            {
                case 2 when data.Length == 4:
                    created = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(data));
                    break;
                case 9 when data.Length == 4:
                    expiration = BinaryPrimitives.ReadUInt32BigEndian(data);
                    break;
                case 27 when data.Length >= 1:
                    flags = data[0];
                    break;
                case 33 when data.Length == 21:
                    issuer = data[1..].ToArray();
                    break;
                case 16 when data.Length == 8:
                    issuer ??= data.ToArray();
                    break;
            }
        }

        foreach (var (subtype, data) in Subpackets(unhashed))
        {
            if (subtype == 16 && data.Length == 8)
            {
                issuer ??= data.ToArray();
            }
        }

        if (issuer is not null && !primary.Fingerprint.AsSpan().EndsWith(issuer))
        {
            return;
        }

        KeyEntry target;
        switch (type)
        {
            case >= 0x10 and <= 0x13 or 0x1F when !current.IsSubkey:
                target = primary;
                break;
            case 0x18 when current.IsSubkey:
                target = current;
                break;
            case 0x20:
                primary.Revoked = true;
                return;
            case 0x28 when current.IsSubkey:
                current.Revoked = true;
                return;
            default:
                return;
        }

        var time = created ?? DateTimeOffset.MinValue;
        if (target.SelfSignatureTime is { } previous && previous > time)
        {
            return;
        }

        target.SelfSignatureTime = time;
        if (flags is not null)
        {
            target.Flags = flags;
        }

        target.ExpirationSeconds = expiration;
    }

    private static List<(int Type, byte[] Data)> Subpackets(ReadOnlySpan<byte> area)
    {
        var result = new List<(int, byte[])>();
        var position = 0;
        while (position < area.Length)
        {
            int length = area[position++];
            if (length >= 192 && length < 255)
            {
                if (position >= area.Length)
                {
                    break;
                }

                length = ((length - 192) << 8) + area[position++] + 192;
            }
            else if (length == 255)
            {
                if (area.Length - position < 4)
                {
                    break;
                }

                length = (int)BinaryPrimitives.ReadUInt32BigEndian(area[position..]);
                position += 4;
            }

            if (length < 1 || length > area.Length - position)
            {
                break;
            }

            result.Add((area[position] & 0x7F, area.Slice(position + 1, length - 1).ToArray()));
            position += length;
        }

        return result;
    }

    /// <summary>Decrypts (when protected) and checks the RSA secret MPIs d, p, q and u.</summary>
    private static RSA ReadRsaSecret(KeyEntry key, string? passphrase)
    {
        var body = key.Packet.Body.Span;
        var position = key.PublicEnd;
        var fingerprint = Convert.ToHexString(key.Fingerprint);
        if (position >= body.Length)
        {
            throw new FormatException("a secret key packet is truncated");
        }

        var usage = body[position++];
        byte[] secret;
        switch (usage)
        {
            case 0:
                secret = body[position..].ToArray();
                break;
            case 254 or 255:
                secret = Decrypt(body, ref position, usage, passphrase, fingerprint);
                break;
            case 253:
                throw new SigningException(
                    $"The OpenPGP key {fingerprint} is protected with AEAD, which zexoro does not read; export it from gpg 2.4 or earlier with its default protection, or without a passphrase.");
            default:
                throw new SigningException($"The OpenPGP key {fingerprint} uses a legacy secret key protection that zexoro does not read; change its passphrase with gpg and export it again.");
        }

        try
        {
            var at = 0;
            var d = OpenPgpPackets.ReadMpi(secret, ref at);
            var p = OpenPgpPackets.ReadMpi(secret, ref at);
            var q = OpenPgpPackets.ReadMpi(secret, ref at);
            OpenPgpPackets.ReadMpi(secret, ref at);
            if (usage == 0)
            {
                if (secret.Length - at < 2)
                {
                    throw new FormatException("a secret key checksum is missing");
                }

                var sum = 0;
                for (var i = 0; i < at; i++)
                {
                    sum = (sum + secret[i]) & 0xFFFF;
                }

                if (sum != BinaryPrimitives.ReadUInt16BigEndian(secret.AsSpan(at)))
                {
                    throw new SigningException($"The OpenPGP key {fingerprint} is damaged: its secret key checksum does not match.");
                }
            }

            return CreateRsa(key.PublicMpis[0], key.PublicMpis[1], d, p, q, fingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> body, ref int position, byte usage, string? passphrase, string fingerprint)
    {
        if (body.Length - position < 2)
        {
            throw new FormatException("a secret key packet is truncated");
        }

        var cipher = body[position++];
        var keyLength = cipher switch
        {
            7 => 16,
            8 => 24,
            9 => 32,
            _ => throw new SigningException(
                $"The OpenPGP key {fingerprint} is protected with symmetric algorithm {cipher.ToString(CultureInfo.InvariantCulture)}; zexoro reads AES-protected keys. "
                + "Export it with 'gpg --s2k-cipher-algo AES256 --export-secret-keys --armor'."),
        };

        var s2kType = body[position++];
        if (s2kType == 101)
        {
            throw new SigningException(
                $"The OpenPGP export has no secret part for key {fingerprint} (a stub, for example of a smartcard key); export a key whose secret part is on this machine.");
        }

        if (s2kType is not (0 or 1 or 3))
        {
            throw new SigningException($"The OpenPGP key {fingerprint} uses S2K type {s2kType.ToString(CultureInfo.InvariantCulture)}, which zexoro does not read.");
        }

        if (body.Length - position < 1)
        {
            throw new FormatException("a secret key packet is truncated");
        }

        var hashName = body[position++] switch
        {
            2 => HashAlgorithmName.SHA1,
            8 => HashAlgorithmName.SHA256,
            9 => HashAlgorithmName.SHA384,
            10 => HashAlgorithmName.SHA512,
            var other => throw new SigningException(
                $"The OpenPGP key {fingerprint} uses S2K hash algorithm {other.ToString(CultureInfo.InvariantCulture)}, which zexoro does not read; export it with '--s2k-digest-algo SHA256'."),
        };

        var salt = ReadOnlySpan<byte>.Empty;
        long count = 0;
        if (s2kType is 1 or 3)
        {
            if (body.Length - position < 8)
            {
                throw new FormatException("a secret key packet is truncated");
            }

            salt = body.Slice(position, 8);
            position += 8;
        }

        if (s2kType == 3)
        {
            if (body.Length - position < 1)
            {
                throw new FormatException("a secret key packet is truncated");
            }

            var coded = body[position++];
            count = (16L + (coded & 15)) << ((coded >> 4) + 6);
        }

        if (body.Length - position < 16)
        {
            throw new FormatException("a secret key packet is truncated");
        }

        var iv = body.Slice(position, 16);
        position += 16;
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new SigningException($"The OpenPGP key {fingerprint} is protected by a passphrase; set its password in the configuration.");
        }

        var key = DeriveKey(Encoding.UTF8.GetBytes(passphrase), salt, count, hashName, keyLength);
        var plain = DecryptCfb(key, iv, body[position..]);
        CryptographicOperations.ZeroMemory(key);

        var ok = false;
        if (usage == 254 && plain.Length >= 20)
        {
#pragma warning disable CA5350 // RFC 9580 defines this integrity check with SHA-1.
            var hash = SHA1.HashData(plain.AsSpan(0, plain.Length - 20));
#pragma warning restore CA5350
            ok = CryptographicOperations.FixedTimeEquals(hash, plain.AsSpan(plain.Length - 20));
            Array.Resize(ref plain, plain.Length - 20);
        }
        else if (usage == 255 && plain.Length >= 2)
        {
            var sum = 0;
            for (var i = 0; i < plain.Length - 2; i++)
            {
                sum = (sum + plain[i]) & 0xFFFF;
            }

            ok = sum == BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(plain.Length - 2));
            Array.Resize(ref plain, plain.Length - 2);
        }

        if (!ok)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new SigningException($"The password of the OpenPGP key {fingerprint} is wrong.");
        }

        return plain;
    }

    /// <summary>The string-to-key derivation of RFC 9580 section 3.7.1 (simple, salted, iterated and salted).</summary>
    internal static byte[] DeriveKey(byte[] passphrase, ReadOnlySpan<byte> salt, long count, HashAlgorithmName hashName, int keyLength)
    {
        var input = new byte[salt.Length + passphrase.Length];
        salt.CopyTo(input);
        passphrase.CopyTo(input, salt.Length);
        var total = Math.Max(count, input.Length);
        var key = new byte[keyLength];
        var filled = 0;
        for (var context = 0; filled < keyLength; context++)
        {
            using var hash = IncrementalHash.CreateHash(hashName);

            // Each further context is preloaded with one more zero byte.
            hash.AppendData(new byte[context]);
            var remaining = total;
            while (remaining > 0)
            {
                var take = (int)Math.Min(remaining, input.Length);
                hash.AppendData(input, 0, take);
                remaining -= take;
            }

            var digest = hash.GetHashAndReset();
            var copy = Math.Min(digest.Length, keyLength - filled);
            Array.Copy(digest, 0, key, filled, copy);
            filled += copy;
        }

        CryptographicOperations.ZeroMemory(input);
        return key;
    }

    /// <summary>AES in OpenPGP's CFB mode with a full-block shift and no resynchronization.</summary>
    private static byte[] DecryptCfb(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = new byte[ciphertext.Length];
        var register = iv.ToArray();
        var stream = new byte[16];
        for (var offset = 0; offset < ciphertext.Length; offset += 16)
        {
            aes.EncryptEcb(register, stream, PaddingMode.None);
            var length = Math.Min(16, ciphertext.Length - offset);
            for (var i = 0; i < length; i++)
            {
                plain[offset + i] = (byte)(ciphertext[offset + i] ^ stream[i]);
            }

            if (length == 16)
            {
                ciphertext.Slice(offset, 16).CopyTo(register);
            }
        }

        return plain;
    }

    /// <summary>Builds the RSA key from OpenPGP's n, e, d, p and q (u = p⁻¹ mod q is recomputed in the form .NET needs).</summary>
    private static RSA CreateRsa(byte[] n, byte[] e, byte[] d, byte[] p, byte[] q, string fingerprint)
    {
        static BigInteger Big(byte[] value) => new(value, isUnsigned: true, isBigEndian: true);
        var bn = Big(n);
        var bp = Big(p);
        var bq = Big(q);
        var bd = Big(d);
        if (bp * bq != bn || bp.IsOne || bq.IsOne)
        {
            throw new SigningException($"The OpenPGP key {fingerprint} is damaged: its secret part does not match its public key.");
        }

        var half = (n.Length + 1) / 2;
        byte[] Pad(BigInteger value, int length)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length > length)
            {
                throw new SigningException($"The OpenPGP key {fingerprint} has RSA primes of unusual sizes, which .NET cannot import.");
            }

            var result = new byte[length];
            bytes.CopyTo(result, length - bytes.Length);
            return result;
        }

        var parameters = new RSAParameters
        {
            Modulus = n,
            Exponent = e,
            D = Pad(bd, n.Length),
            P = Pad(bp, half),
            Q = Pad(bq, half),
            DP = Pad(bd % (bp - 1), half),
            DQ = Pad(bd % (bq - 1), half),
            InverseQ = Pad(BigInteger.ModPow(bq, bp - 2, bp), half),
        };
        var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(parameters);
            return rsa;
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new SigningException($"The OpenPGP key {fingerprint} cannot be used: {ex.Message}", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
            CryptographicOperations.ZeroMemory(parameters.P);
            CryptographicOperations.ZeroMemory(parameters.Q);
            CryptographicOperations.ZeroMemory(parameters.DP);
            CryptographicOperations.ZeroMemory(parameters.DQ);
            CryptographicOperations.ZeroMemory(parameters.InverseQ);
        }
    }
}
