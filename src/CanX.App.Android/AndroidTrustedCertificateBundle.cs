// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace CanX.App.Android;

internal static class AndroidTrustedCertificateBundle
{
    private static readonly string[] CertificateDirectories =
    [
        "/apex/com.android.conscrypt/cacerts",
        "/system/etc/security/cacerts",
    ];

    public static string Create(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var sourceDirectory = CertificateDirectories.FirstOrDefault(Directory.Exists)
            ?? throw new DirectoryNotFoundException("Android's system CA certificate directory is unavailable.");
        var certificates = Directory.EnumerateFiles(sourceDirectory)
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .Where(static certificate => certificate.Contains(
                "-----BEGIN CERTIFICATE-----",
                StringComparison.Ordinal))
            .ToArray();
        if (certificates.Length == 0)
        {
            throw new InvalidDataException("Android's system trust store did not contain any PEM certificates.");
        }

        var bundle = new StringBuilder(certificates.Sum(static certificate => certificate.Length + 1));
        foreach (var certificate in certificates)
        {
            bundle.Append(certificate.TrimEnd()).Append('\n');
        }

        var path = Path.Combine(dataDirectory, "android-system-ca.pem");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, bundle.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }
}
