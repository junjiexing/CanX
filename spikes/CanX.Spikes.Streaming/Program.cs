namespace CanX.Spikes.Streaming;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var port))
        {
            Console.Error.WriteLine(
                "Usage: CanX.Spikes.Streaming <source-uri> <destination-path> <gateway-port>");
            return 2;
        }

        try
        {
            using var cancellation = new CancellationTokenSource();
            var sourceUri = new Uri(args[0], UriKind.Absolute);
            var destinationPath = Path.GetFullPath(args[1]);
            var download = new AriaDownloadCoordinator(sourceUri.AbsoluteUri, destinationPath);
            await download.StartAsync(cancellation.Token);

            await using var gateway = new RangeGateway(download, destinationPath, sourceUri, port);
            var gatewayTask = gateway.RunAsync(cancellation.Token);
            Console.WriteLine($"CANX_GATEWAY_READY url=http://127.0.0.1:{port}/media");

            await Task.WhenAll(download.Completion, gateway.ShutdownRequested);
            cancellation.Cancel();
            await gatewayTask;
            Console.WriteLine("CANX_STREAM_GATEWAY_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
