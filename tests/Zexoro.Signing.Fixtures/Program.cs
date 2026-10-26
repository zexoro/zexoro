using System.Security.Cryptography.X509Certificates;
using Zexoro.Signing;
using Zexoro.Signing.Fixtures;

// Usage: Zexoro.Signing.Fixtures [--timestamp <url>] <output-dir> [extra PE files to sign...]
//
// For each throwaway credential <name> (rsa, ecdsa, chain) this writes:
//   <name>.root.cer / <name>.root.pem   the certificate to trust when verifying
//   <name>.<fixture>.exe                signed PE files
// CI verifies every <name>.*.exe against <name>.root.* with an independent tool.

Uri? timestampUrl = null;
if (args.Length >= 2 && args[0] == "--timestamp")
{
    timestampUrl = new Uri(args[1]);
    args = args[2..];
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Zexoro.Signing.Fixtures [--timestamp <url>] <output-dir> [extra PE files...]");
    return 2;
}

var output = Directory.CreateDirectory(args[0]).FullName;
var extras = args.Skip(1).Select(Path.GetFullPath).ToArray();
const string password = "throwaway";

var rsa = TestCertificates.CreateSelfSigned(ecdsa: false, "zexoro test signer (RSA)");
var ecdsa = TestCertificates.CreateSelfSigned(ecdsa: true, "zexoro test signer (ECDSA)");
var (root, intermediate, leaf) = TestCertificates.CreateChain();

var credentials = new (string Name, X509Certificate2 Trust, X509Certificate2Collection Pfx)[]
{
    ("rsa", rsa, [rsa]),
    ("ecdsa", ecdsa, [ecdsa]),
    ("chain", root, [leaf, intermediate]),
};

var signed = 0;
foreach (var (name, trust, pfxContents) in credentials)
{
    File.WriteAllBytes(Path.Combine(output, $"{name}.root.cer"), trust.RawData);
    File.WriteAllText(Path.Combine(output, $"{name}.root.pem"), trust.ExportCertificatePem() + "\n");

    // Go through a PFX file, as users do.
    var pfxPath = Path.Combine(output, $"{name}.pfx");
    File.WriteAllBytes(pfxPath, pfxContents.Export(X509ContentType.Pkcs12, password)!);
    using var credential = SigningCredential.LoadPfx(pfxPath, password);
    File.Delete(pfxPath);

    var options = new SigningOptions
    {
        Credential = credential,
        Description = "zexoro signing fixture",
        DescriptionUrl = new Uri("https://github.com/zexoro/zexoro"),
        TimestampUrl = timestampUrl,
    };

    var targets = new List<string>
    {
        Write($"{name}.pe32plus.exe", TestPe.Build(pe32Plus: true, overlayLength: 13)),
        Write($"{name}.pe32.exe", TestPe.Build(pe32Plus: false)),
    };

    foreach (var extra in extras)
    {
        var target = Path.Combine(output, $"{name}.{Path.GetFileNameWithoutExtension(extra)}.exe");
        File.Copy(extra, target, overwrite: true);
        targets.Add(target);
    }

    if (name == "rsa")
    {
        // Signed first with a different key, then re-signed: only the second signature may remain.
        var resigned = Write($"{name}.resigned.exe", TestPe.Build(pe32Plus: true, seed: 7));
        using (var other = new SigningCredential(ecdsa))
        {
            AuthenticodeSigner.SignFile(resigned, new SigningOptions { Credential = other });
        }

        targets.Add(resigned);
    }

    foreach (var target in targets)
    {
        AuthenticodeSigner.SignFile(target, options);
        if (!AuthenticodeSigner.IsSigned(target))
        {
            Console.Error.WriteLine($"{target}: not signed after signing");
            return 1;
        }

        Console.WriteLine($"signed {target}");
        signed++;
    }
}

Console.WriteLine($"{signed} files signed in {output}");
return 0;

string Write(string fileName, byte[] contents)
{
    var path = Path.Combine(output, fileName);
    File.WriteAllBytes(path, contents);
    return path;
}
