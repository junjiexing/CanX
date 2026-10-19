// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using CanX.App;
using Microsoft.Win32;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class FileAssociationTests
{
    [Fact]
    public void WindowsRegistration_WritesPerUserProgIdsCapabilitiesAndOpenWith()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyRegistrationWritesKeys();
    }

    [Fact]
    public void WindowsRegistration_UnregisterRemovesOnlyCanXEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyUnregisterRemovesOnlyCanXEntries();
    }

    [Fact]
    public void WindowsRegistration_RequiresAnExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyRegistrationRequiresExecutable();
    }

    [Theory]
    [InlineData(".mkv", "CanX.Media")]
    [InlineData(".FLAC", "CanX.Media")]
    [InlineData("m3u", "CanX.Playlist")]
    [InlineData(".metalink", "CanX.Download")]
    public void WindowsRegistration_MapsExtensionsToProgIds(string extension, string progId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(progId, ProgIdFor(extension));
    }

    [Fact]
    public void SingleInstance_MessageRoundTripKeepsOnlyAbsolutePaths()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "movie.mkv");
        var message = SingleInstanceGate.EncodeMessage([absolute, "relative.mkv", " "]);

        Assert.Equal([absolute], SingleInstanceGate.DecodeMessage(message));
    }

    [Fact]
    public void SingleInstance_IdentityIsStablePerDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CanX");

        Assert.Equal(
            SingleInstanceGate.CreateIdentity(root),
            SingleInstanceGate.CreateIdentity(root + Path.DirectorySeparatorChar));
        Assert.NotEqual(
            SingleInstanceGate.CreateIdentity(root),
            SingleInstanceGate.CreateIdentity(root + "-other"));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRegistrationWritesKeys()
    {
        using var fixture = new RegistryFixture();
        var service = fixture.CreateService();

        service.Register(CanXFileFormats.Groups.SelectMany(static group => group.Extensions));

        using var classes = fixture.Open(@"Software\Classes");
        using var media = classes.OpenSubKey(@"CanX.Media\shell\open\command")!;
        Assert.Equal($"\"{fixture.Executable}\" \"%1\"", media.GetValue(string.Empty));
        Assert.Contains("CanX.Download", classes.OpenSubKey(@".torrent\OpenWithProgids")!.GetValueNames());
        Assert.Contains("CanX.Playlist", classes.OpenSubKey(@".m3u8\OpenWithProgids")!.GetValueNames());
        Assert.Contains("CanX.Media", classes.OpenSubKey(@".mkv\OpenWithProgids")!.GetValueNames());
        Assert.Contains(".flac", classes.OpenSubKey($@"Applications\{Path.GetFileName(fixture.Executable)}\SupportedTypes")!.GetValueNames());

        using var capabilities = fixture.Open(@"Software\CanX\Capabilities\FileAssociations");
        Assert.Equal("CanX.Media", capabilities.GetValue(".mp4"));
        Assert.Equal("CanX.Download", capabilities.GetValue(".meta4"));
        using var registered = fixture.Open(@"Software\RegisteredApplications");
        Assert.Equal(@"Software\CanX\Capabilities", registered.GetValue("CanX"));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyUnregisterRemovesOnlyCanXEntries()
    {
        using var fixture = new RegistryFixture();
        using (var other = fixture.Root.CreateSubKey($@"{fixture.Prefix}\Software\Classes\.mkv\OpenWithProgids"))
        {
            other.SetValue("OtherPlayer.mkv", Array.Empty<byte>(), RegistryValueKind.None);
        }
        var service = fixture.CreateService();

        service.Register(CanXFileFormats.Groups.SelectMany(static group => group.Extensions));
        service.Unregister();

        using var classes = fixture.Open(@"Software\Classes");
        Assert.Null(classes.OpenSubKey("CanX.Media"));
        Assert.Null(classes.OpenSubKey($@"Applications\{Path.GetFileName(fixture.Executable)}"));
        Assert.Equal(["OtherPlayer.mkv"], classes.OpenSubKey(@".mkv\OpenWithProgids")!.GetValueNames());
        Assert.Null(fixture.Root.OpenSubKey($@"{fixture.Prefix}\Software\CanX\Capabilities"));
        Assert.DoesNotContain("CanX", fixture.Open(@"Software\RegisteredApplications").GetValueNames());
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRegistrationRequiresExecutable()
    {
        using var fixture = new RegistryFixture();
        var service = new WindowsFileAssociationService(fixture.Root, fixture.Prefix, "CanX.App.dll", notifyShell: false);

        Assert.False(service.CanChange);
        Assert.NotNull(service.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => service.Register([".mkv"]));
    }

    [SupportedOSPlatform("windows")]
    private static string ProgIdFor(string extension) => WindowsFileAssociationService.ProgIdFor(extension);

    [SupportedOSPlatform("windows")]
    private sealed class RegistryFixture : IDisposable
    {
        public RegistryFixture()
        {
            Prefix = $@"Software\CanX.Tests\{Guid.NewGuid():N}";
            Executable = Path.Combine(Path.GetTempPath(), $"canx-association-{Guid.NewGuid():N}.exe");
            File.WriteAllBytes(Executable, []);
        }

        public RegistryKey Root => Registry.CurrentUser;

        public string Prefix { get; }

        public string Executable { get; }

        public WindowsFileAssociationService CreateService() =>
            new(Root, Prefix, Executable, notifyShell: false);

        public RegistryKey Open(string path) =>
            Root.OpenSubKey($@"{Prefix}\{path}") ?? throw new KeyNotFoundException(path);

        public void Dispose()
        {
            Root.DeleteSubKeyTree(Prefix, throwOnMissingSubKey: false);
            using (var tests = Root.OpenSubKey(@"Software\CanX.Tests", writable: true))
            {
                if (tests is { SubKeyCount: 0, ValueCount: 0 })
                {
                    Root.DeleteSubKey(@"Software\CanX.Tests", throwOnMissingSubKey: false);
                }
            }

            File.Delete(Executable);
        }
    }
}
