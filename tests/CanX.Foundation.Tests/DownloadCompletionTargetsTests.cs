// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Core.Downloads;
using CanX.Core.Events;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadCompletionTargetsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "canx-completion-root");

    [Theory]
    [InlineData("movie.mkv", "movie.mkv")]
    [InlineData("season/episode.mp4", "season/episode.mp4")]
    [InlineData("setup.exe", "")]
    [InlineData("readme.lnk", "")]
    [InlineData("season/run.bat", "season")]
    [InlineData("../outside.mkv", "")]
    public void Resolve_OpensOnlyMediaInsideTaskDirectory(string file, string expectedRelative)
    {
        var target = DownloadCompletionTargets.Resolve(CreateSnapshot(DownloadCompletionAction.OpenFile, file));

        var expected = expectedRelative.Length == 0
            ? Path.GetFullPath(Root)
            : Path.GetFullPath(Path.Combine(Root, expectedRelative));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void Resolve_OpenDirectoryReturnsTaskDirectory()
    {
        var target = DownloadCompletionTargets.Resolve(CreateSnapshot(DownloadCompletionAction.OpenDirectory, "a.exe"));

        Assert.Equal(Path.GetFullPath(Root), target);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=video&tr=udp%3A%2F%2Ftracker.example%3A1337", false)]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=https%3A%2F%2Ftracker.example%2Fannounce%3Fpasskey%3Dsecret", true)]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&ws=https%3A%2F%2Fuser%3Apass%40seed.example%2Ff", true)]
    [InlineData("https://example.test/file.mkv?token=abc", true)]
    [InlineData("https://example.test/file.mkv", false)]
    public void IsSensitiveUri_TreatsOnlyPrivateMagnetPartsAsSecrets(string uri, bool expected)
    {
        Assert.Equal(expected, DownloadSecretClassifier.IsSensitiveUri(uri));
    }

    [Fact]
    public void EventBus_IsolatesFailingSubscribers()
    {
        var failures = new List<Exception>();
        var bus = new InMemoryEventBus(failures.Add);
        var delivered = 0;
        using var failing = bus.Subscribe<ProbeEvent>(_ => throw new InvalidOperationException("boom"));
        using var healthy = bus.Subscribe<ProbeEvent>(_ => delivered++);

        bus.Publish(new ProbeEvent());

        Assert.Equal(1, delivered);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    private sealed record ProbeEvent : IAppEvent;

    private static DownloadTaskSnapshot CreateSnapshot(DownloadCompletionAction action, string file) => new()
    {
        Id = Guid.NewGuid(),
        EngineGid = "0000000000000001",
        Kind = DownloadSourceKind.Uri,
        DisplayName = "task",
        Source = "https://example.test/file",
        DestinationDirectory = Root,
        CompletionAction = action,
        Files =
        [
            new DownloadFileSnapshot { Index = 1, Path = file, IsSelected = true },
        ],
    };
}
