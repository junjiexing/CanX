// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using CanX.Plugins.Manifest;
using CanX.Plugins.Packaging;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginPackageInstallerTests
{
    [Fact]
    public void Install_ValidPackage_ExtractsAtomicallyAndReturnsHash()
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(
            Manifest("1.0.0", "dist/main.mjs"),
            ("dist/main.mjs", "export const value = 42;"),
            ("assets/icon.txt", "icon"));

        var installation = fixture.Installer.Install(package);

        Assert.Equal("org.canx.example", installation.Manifest.Id);
        Assert.Equal(64, installation.PackageSha256.Length);
        Assert.Equal(
            "export const value = 42;",
            File.ReadAllText(Path.Combine(installation.DirectoryPath, "dist", "main.mjs")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".staging", "content")));
    }

    [Fact]
    public void Install_ExistingPlugin_RequiresExplicitUpgradeAndPreservesOldVersion()
    {
        using var fixture = new InstallerFixture();
        using var first = CreatePackage(Manifest("1.0.0"), ("main.mjs", "version-one"));
        fixture.Installer.Install(first);

        using var second = CreatePackage(Manifest("2.0.0"), ("main.mjs", "version-two"));
        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(second));

        Assert.Contains("upgrade was not authorized", exception.Message, StringComparison.Ordinal);
        Assert.Equal("version-one", File.ReadAllText(Path.Combine(fixture.Root, "org.canx.example", "main.mjs")));
    }

    [Fact]
    public void Install_AuthorizedUpgrade_ReplacesWholePluginDirectory()
    {
        using var fixture = new InstallerFixture();
        using var first = CreatePackage(
            Manifest("1.0.0"),
            ("main.mjs", "version-one"),
            ("old.txt", "removed"));
        fixture.Installer.Install(first);

        using var second = CreatePackage(Manifest("2.0.0"), ("main.mjs", "version-two"));
        var installation = fixture.Installer.Install(second, allowUpgrade: true);

        Assert.Equal("2.0.0", installation.Manifest.Version);
        Assert.Equal("version-two", File.ReadAllText(Path.Combine(installation.DirectoryPath, "main.mjs")));
        Assert.False(File.Exists(Path.Combine(installation.DirectoryPath, "old.txt")));
        Assert.Equal("1.0.0", fixture.Installer.ReadRollbackManifest("org.canx.example")?.Version);

        fixture.Installer.SwapWithRollback("org.canx.example");

        Assert.Equal("version-one", File.ReadAllText(Path.Combine(installation.DirectoryPath, "main.mjs")));
        Assert.Equal("2.0.0", fixture.Installer.ReadRollbackManifest("org.canx.example")?.Version);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("folder\\outside.txt")]
    public void Install_UnsafeArchivePath_IsRejected(string unsafePath)
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(
            Manifest("1.0.0"),
            ("main.mjs", "module"),
            (unsafePath, "unsafe"));

        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("unsafe path", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public void Install_MissingDeclaredEntry_IsRejected()
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(Manifest("1.0.0", "missing.mjs"), ("main.mjs", "module"));

        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "org.canx.example")));
    }

    [Fact]
    public void Install_EntryPathCasingMismatch_IsRejectedForPortablePackages()
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(Manifest("1.0.0", "Main.mjs"), ("main.mjs", "module"));

        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_UiAssetIcon_MustExistAndContainSupportedRasterData()
    {
        using var fixture = new InstallerFixture();
        var manifest = Manifest("1.0.0").Replace(
            "\"entry\": \"main.mjs\"",
            "\"entry\": \"main.mjs\", \"ui\": { \"title\": \"Tools\", \"icon\": { \"asset\": \"assets/icon.png\" } }, \"permissions\": [{ \"capability\": \"ui.page\" }]",
            StringComparison.Ordinal);
        using var missing = CreatePackage(manifest, ("main.mjs", "module"));

        var missingException = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(missing));

        Assert.Contains("does not exist", missingException.Message, StringComparison.Ordinal);

        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0 };
        using var valid = CreateBinaryPackage(new Dictionary<string, byte[]>
        {
            ["canx-plugin.json"] = Encoding.UTF8.GetBytes(manifest),
            ["main.mjs"] = Encoding.UTF8.GetBytes("module"),
            ["assets/icon.png"] = png,
        });

        var installation = fixture.Installer.Install(valid);

        Assert.Equal(PluginUiIconKind.Asset, installation.Manifest.Ui?.Icon.Kind);
        Assert.True(File.Exists(Path.Combine(installation.DirectoryPath, "assets", "icon.png")));
    }

    [Fact]
    public void Install_CaseInsensitiveDuplicatePath_IsRejected()
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(
            Manifest("1.0.0"),
            ("main.mjs", "first"),
            ("MAIN.MJS", "second"));

        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("duplicate path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_SymbolicLinkEntry_IsRejected()
    {
        using var fixture = new InstallerFixture();
        using var package = CreatePackage(Manifest("1.0.0"), ("main.mjs", "module"));
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            var link = archive.CreateEntry("linked-secret");
            link.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(link.Open(), new UTF8Encoding(false));
            writer.Write("../../secret");
        }

        package.Position = 0;
        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("symbolic link", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ArchiveOverConfiguredLimit_IsRejected()
    {
        using var fixture = new InstallerFixture(
            new PluginPackageLimits(64, 1024, 1024, 10));
        using var package = CreatePackage(Manifest("1.0.0"), ("main.mjs", new string('x', 256)));

        var exception = Assert.Throws<PluginPackageException>(() => fixture.Installer.Install(package));

        Assert.Contains("compressed bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_SignedPackage_VerifiesEveryFileAndReportsTrust()
    {
        using var fixture = new InstallerFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["canx-plugin.json"] = Encoding.UTF8.GetBytes(Manifest("1.0.0")),
            ["main.mjs"] = Encoding.UTF8.GetBytes("signed-module"),
        };
        var signature = PluginPackageSigner.CreateSignatureDocument(files, key);
        using var package = CreateBinaryPackage(files, signature);
        var keyId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var trustedInstaller = new PluginPackageInstaller(
            fixture.Root,
            signaturePolicy: new PluginSignaturePolicy
            {
                RequireSignature = true,
                RequireTrustedSigner = true,
                TrustedKeyIds = new HashSet<string>([keyId], StringComparer.Ordinal),
            });

        var installation = trustedInstaller.Install(package);

        Assert.Equal(keyId, installation.Signature?.KeyId);
        Assert.True(installation.Signature?.IsTrusted);
    }

    [Fact]
    public void Install_TamperedSignedPackage_IsRejectedBeforeUpgrade()
    {
        using var fixture = new InstallerFixture();
        using var original = CreatePackage(Manifest("1.0.0"), ("main.mjs", "original"));
        fixture.Installer.Install(original);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["canx-plugin.json"] = Encoding.UTF8.GetBytes(Manifest("2.0.0")),
            ["main.mjs"] = Encoding.UTF8.GetBytes("signed"),
        };
        var signature = PluginPackageSigner.CreateSignatureDocument(signedFiles, key);
        signedFiles["main.mjs"] = Encoding.UTF8.GetBytes("tampered");
        using var tampered = CreateBinaryPackage(signedFiles, signature);

        var exception = Assert.Throws<PluginPackageException>(() =>
            fixture.Installer.Install(tampered, allowUpgrade: true));

        Assert.Contains("modified", exception.Message, StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Root, "org.canx.example", "main.mjs")));
    }

    [Fact]
    public void Install_UnsignedPackage_IsRejectedWhenPolicyRequiresSignature()
    {
        using var fixture = new InstallerFixture();
        var installer = new PluginPackageInstaller(
            fixture.Root,
            signaturePolicy: new PluginSignaturePolicy { RequireSignature = true });
        using var package = CreatePackage(Manifest("1.0.0"), ("main.mjs", "module"));

        var exception = Assert.Throws<PluginPackageException>(() => installer.Install(package));

        Assert.Contains("must have a valid signature", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_P384Key_IsRejectedInsteadOfBeingMislabeledAsP256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["canx-plugin.json"] = Encoding.UTF8.GetBytes(Manifest("1.0.0")),
            ["main.mjs"] = Encoding.UTF8.GetBytes("module"),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            PluginPackageSigner.CreateSignatureDocument(files, key));

        Assert.Contains("P-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_NodeCliSignedPackage_IsCryptographicallyCompatible()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null || !TryRunNode(["--version"], out _))
        {
            return;
        }
        using var fixture = new InstallerFixture();
        var workspace = Path.Combine(fixture.Root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "dist"));
        File.WriteAllText(Path.Combine(workspace, "canx-plugin.json"), Manifest("1.0.0", "dist/main.mjs"));
        File.WriteAllText(Path.Combine(workspace, "package.json"), "{\"files\":[\"dist\"]}");
        File.WriteAllText(Path.Combine(workspace, "dist", "main.mjs"), "export const signed = true;");
        var keyPath = Path.Combine(fixture.Root, "signing.pem");
        var packagePath = Path.Combine(fixture.Root, "node-signed.canxplugin");
        var cliPath = Path.Combine(repositoryRoot, "sdk", "canx-plugin-api", "bin", "canx-plugin.mjs");
        Assert.True(TryRunNode([cliPath, "keygen", keyPath], out var keygenError), keygenError);
        Assert.True(TryRunNode(
            [cliPath, "pack", workspace, "--out", packagePath, "--key", keyPath],
            out var packError), packError);
        var installer = new PluginPackageInstaller(
            Path.Combine(fixture.Root, "installed"),
            signaturePolicy: new PluginSignaturePolicy { RequireSignature = true });

        using var package = File.OpenRead(packagePath);
        var installation = installer.Install(package);

        Assert.Equal("ecdsa-p256-sha256", installation.Signature?.Algorithm);
        Assert.Equal(64, installation.Signature?.KeyId.Length);
    }

    private static MemoryStream CreatePackage(string manifest, params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "canx-plugin.json", manifest);
            foreach (var (path, content) in files)
            {
                WriteEntry(archive, path, content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateBinaryPackage(
        IReadOnlyDictionary<string, byte[]> files,
        byte[]? signature = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
            if (signature is not null)
            {
                var signatureEntry = archive.CreateEntry("canx-signature.json", CompressionLevel.NoCompression);
                using var signatureStream = signatureEntry.Open();
                signatureStream.Write(signature);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Manifest(string version, string entry = "main.mjs") =>
        $$"""
        {
          "manifestVersion": 1,
          "id": "org.canx.example",
          "name": "Example",
          "version": "{{version}}",
          "apiVersion": "2.0",
          "entry": "{{entry}}"
        }
        """;

    private static string? FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "sdk", "canx-plugin-api", "bin", "canx-plugin.mjs")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static bool TryRunNode(IReadOnlyList<string> arguments, out string error)
    {
        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Unable to start Node.js.";
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            error = string.IsNullOrWhiteSpace(standardError) ? output : standardError;
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private sealed class InstallerFixture : IDisposable
    {
        public InstallerFixture(PluginPackageLimits? limits = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "CanX.PluginTests", Guid.NewGuid().ToString("N"));
            Installer = new PluginPackageInstaller(Root, limits);
        }

        public string Root { get; }

        public PluginPackageInstaller Installer { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
