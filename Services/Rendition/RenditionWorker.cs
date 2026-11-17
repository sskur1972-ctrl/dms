namespace DMS.Web.Services.Rendition
{
    public sealed class RenditionWorker : BackgroundService
    {
        private static readonly SemaphoreSlim _signal = new(0, 1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RenditionWorker> _logger;

        public RenditionWorker(IServiceScopeFactory scopeFactory, ILogger<RenditionWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public static void Nudge()
        {
            if (_signal.CurrentCount == 0)
            {
                try { _signal.Release(); }
                catch (SemaphoreFullException) { }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Small startup delay so Kestrel finishes binding before background jobs run
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<RenditionProcessor>();

                    int handled;
                    do
                    {
                        handled = await processor.RunBatchAsync(5, stoppingToken);
                    } while (handled > 0 && !stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in RenditionWorker loop.");
                }

                // Wait until Nudge() is called or 15 seconds elapse
                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
