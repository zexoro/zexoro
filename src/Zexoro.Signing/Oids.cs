namespace Zexoro.Signing;

/// <summary>Object identifiers used by Authenticode, CMS and RFC 3161.</summary>
internal static class Oids
{
    // CMS / PKCS #7 / PKCS #9
    public const string SignedData = "1.2.840.113549.1.7.2";
    public const string ContentType = "1.2.840.113549.1.9.3";
    public const string MessageDigest = "1.2.840.113549.1.9.4";

    // Algorithms
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";
    public const string RsaEncryption = "1.2.840.113549.1.1.1";
    public const string EcPublicKey = "1.2.840.10045.2.1";
    public const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";

    // Authenticode
    public const string SpcIndirectDataContent = "1.3.6.1.4.1.311.2.1.4";
    public const string SpcStatementType = "1.3.6.1.4.1.311.2.1.11";
    public const string SpcSpOpusInfo = "1.3.6.1.4.1.311.2.1.12";
    public const string SpcPeImageData = "1.3.6.1.4.1.311.2.1.15";
    public const string SpcIndividualCodeSigning = "1.3.6.1.4.1.311.2.1.21";
    public const string Rfc3161CounterSignature = "1.3.6.1.4.1.311.3.3.1";

    // RFC 3161
    public const string TstInfo = "1.2.840.113549.1.9.16.1.4";
}
