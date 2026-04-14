using LogWatcher.Configuration;
using LogWatcher.Services;
using Microsoft.Extensions.Options;

namespace LogWatcher.Workers;

public class FreshServiceWorker : BackgroundService
{
    private readonly FreshServiceService _freshService;
    private readonly FreshServiceSettings _settings;
    private readonly ILogger<FreshServiceWorker> _logger;

    public FreshServiceWorker(
        FreshServiceService freshService,
        IOptions<AppSettings> options,
        ILogger<FreshServiceWorker> logger)
    {
        _freshService = freshService;
        _settings = options.Value.FreshService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_freshService.IsEnabled)
        {
            _logger.LogInformation("FreshService worker is disabled (AppSettings:FreshService:Enabled=false)");
            return;
        }

        _logger.LogInformation("FreshService worker started. Poll interval: {Interval}s", _settings.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var triage = await _freshService.AnalyzeNewTicketsAsync(stoppingToken);

                if (triage.Count == 0)
                {
                    _logger.LogDebug("FreshService: no new tickets requiring triage");
                }
                else
                {
                    foreach (var item in triage)
                    {
                        _logger.LogInformation(
                            "FreshService ticket #{TicketId} matched closed ticket #{MatchedId} (score {Score:F2}). Team={Team}. AssignmentUpdated={AssignmentUpdated}. FixHint={FixHint}",
                            item.NewTicketId,
                            item.MatchedClosedTicketId,
                            item.SimilarityScore,
                            item.ResponsibleTeam,
                            item.AssignmentUpdated,
                            item.ExistingFix ?? "none");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in FreshService worker cycle");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("FreshService worker stopped");
    }
}
