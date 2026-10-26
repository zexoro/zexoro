# 0004: Authenticode signing

- Status: accepted (stage 3, step 6 of `0003-mvp.md`)
- Date: 2026-10-26
- Scope: `Zexoro.Signing` — sign PE files from a PFX with RSA or ECDSA keys and an optional RFC 3161 timestamp, using only the base class library.

## API

```csharp
using var credential = SigningCredential.LoadPfx(path, password);   // or new SigningCredential(cert, chain)
AuthenticodeSigner.SignFile("app.exe", new SigningOptions
{
    Credential   = credential,
    TimestampUrl = new Uri("http://timestamp.digicert.com"),        // optional
    Description  = "Example Notes",                                  // optional, SpcSpOpusInfo
});
bool signed = AuthenticodeSigner.IsSigned("app.exe");
```

Every failure (not a PE file, wrong password, no private key, unreachable or refusing timestamp authority) is a `SigningException` with a message fit for the user. The digest, signature and timestamp are produced before the file is touched, so a failure leaves the file unchanged. `SigningOptions.HttpClient` replaces the HTTP client, which lets tests run the timestamp path offline.

## Image digest

SHA-256 over the file up to the certificate table (or the end of the file), skipping the optional header `CheckSum` and the certificate table data directory entry. When the file length is not a multiple of 8, the zero padding that precedes the new certificate table is part of the digest.

The specification describes the same coverage as headers, then sections ordered by `PointerToRawData`, then remaining data. For images whose sections are contiguous — what linkers produce — both readings hash the same bytes (a unit test checks this against a literal implementation of the specification). zexoro hashes linearly, as `signtool` and `osslsigncode` do in practice, which also leaves no unsigned gaps.

Supported: PE32 and PE32+. Rejected: a certificate table that is not at the end of the file.

## Signature

PKCS #7 `SignedData` (version 1) with `SpcIndirectDataContent` (`SpcPeImageData` with empty flags and the `<<<Obsolete>>>` file link, plus `DigestInfo`), all certificates of the credential, and one `SignerInfo` (issuer and serial number) with the authenticated attributes contentType, messageDigest, SpcSpOpusInfo and SpcStatementType (individual). RSA uses PKCS #1 v1.5; ECDSA uses `ecdsa-with-SHA256` with a DER signature. Written with `AsnWriter`, because `System.Security.Cryptography.Pkcs` is not in the shared framework.

The timestamp is requested over the signature value (SHA-256 imprint, random nonce, `certReq`) and embedded as the unauthenticated attribute `1.3.6.1.4.1.311.3.3.1`. The response must be granted and carry the requested imprint and nonce. The authority's signature on the token is not checked by zexoro; Windows checks it on verification.

## File update

The old certificate table is dropped, the file is padded to 8 bytes, and one `WIN_CERTIFICATE` (revision 0x0200, type `PKCS_SIGNED_DATA`, padded to 8 bytes; `dwLength` includes the padding) is appended. The data directory entry and the PE checksum are then rewritten. Re-signing therefore replaces any existing signature; appended (nested) signatures are not supported.

## Verification

- Unit tests on all CI platforms: digests against values calculated by `osslsigncode`, against the specification's procedure, checksum against a reference implementation (and against `CheckSumMappedFile` on Windows), CMS structure read back with `AsnReader` and signatures checked with the certificate's public key, re-signing, PFX loading, and the timestamp path against an in-process fake authority.
- Independent tools in CI: `tests/Zexoro.Signing.Fixtures` signs fixture images with throwaway certificates; Linux runs `osslsigncode verify` (including its PE checksum check), Windows runs `Get-AuthenticodeSignature` with the throwaway roots trusted temporarily, and also signs the published `zexoro.exe`.
- Manually verified: signing with the DigiCert timestamp authority, and re-signing a Microsoft-signed DLL, both accepted by `osslsigncode verify`.
