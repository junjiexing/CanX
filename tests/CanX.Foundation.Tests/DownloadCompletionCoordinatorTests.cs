// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.Application.Downloads;
using CanX.Core.Downloads;
using CanX.Core.Events;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class DownloadCompletionCoordinatorTests
{
    [Fact]
    public async Task CompletedTask_IsHandledExactlyOnceAndPublishesResult()
    {
        var eventBus = new InMemoryEventBus();
        var handler = new RecordingHandler();
        DownloadCompletionHandled? result = null;
        using var subscription = eventBus.Subscribe<DownloadCompletionHandled>(value => result = value);
        await using var coordinator = new DownloadCompletionCoordinator(eventBus, handler);
        var snapshot = CreateSnapshot(DownloadState.Complete);

        eventBus.Publish(new DownloadTaskChanged(snapshot));
        eventBus.Publish(new DownloadTaskChanged(snapshot));
        await WaitUntilAsync(() => result is not null);

        Assert.Equal(1, handler.CallCount);
        Assert.True(result!.NotificationSent);
        Assert.True(result.ActionExecuted);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CompletionWithoutConfiguredEffects_IsIgnored()
    {
        var eventBus = new InMemoryEventBus();
        var handler = new RecordingHandler();
        await using var coordinator = new DownloadCompletionCoordinator(eventBus, handler);

        eventBus.Publish(new DownloadTaskChanged(CreateSnapshot(DownloadState.Complete) with
        {
            NotifyOnCompletion = false,
            CompletionAction = DownloadCompletionAction.None,
        }));
        await Task.Delay(30);

        Assert.Equal(0, handler.CallCount);
    }

    private static DownloadTaskSnapshot CreateSnapshot(DownloadState state) => new()
    {
        Id = Guid.NewGuid(),
        EngineGid = "0000000000000001",
        Kind = DownloadSourceKind.Uri,
        Source = "https://example.test/file.bin",
        DestinationDirectory = Path.GetTempPath(),
        DisplayName = "file.bin",
        State = state,
        NotifyOnCompletion = true,
        CompletionAction = DownloadCompletionAction.OpenDirectory,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingHandler : IDownloadCompletionHandler
    {
        public int CallCount { get; private set; }

        public ValueTask<(bool NotificationSent, bool ActionExecuted)> HandleAsync(
            DownloadTaskSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult((true, true));
        }
    }
}
