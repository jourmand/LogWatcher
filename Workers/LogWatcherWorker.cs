using LogWatcher.Configuration;
using LogWatcher.Models;
using LogWatcher.Services;
using Microsoft.Extensions.Options;

namespace LogWatcher.Workers;

/// <summary>
/// Always-running background service:
/// 1. Polls Elasticsearch for new error logs
/// 2. Runs spike detection on the batch
/// 3. Classifies errors with Claude AI
/// 4. Deduplicates and rate-limits
/// 5. Creates GitHub issues assigned to Copilot agent
/// 6. Sends Slack notifications
/// </summary>
public class LogWatcherWorker : BackgroundService
{
    private readonly ElasticsearchPoller _esPoller;
    private readonly ErrorClassifier     _classifier;
    private readonly DeduplicationStore  _dedup;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WatcherSettings     _watcherSettings;
    private readonly GitHubSettings      _githubSettings;
    private readonly ILogger<LogWatcherWorker> _logger;

    public LogWatcherWorker(
        ElasticsearchPoller esPoller,
        ErrorClassifier classifier,
        DeduplicationStore dedup,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> options,
        ILogger<LogWatcherWorker> logger)
    {
        _esPoller        = esPoller;
        _classifier      = classifier;
        _dedup           = dedup;
        _scopeFactory    = scopeFactory;
        _watcherSettings = options.Value.Watcher;
        _githubSettings  = options.Value.GitHub;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LogWatcher started. Poll interval: {Interval}s",
            _watcherSettings.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in log watcher cycle");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_watcherSettings.PollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("LogWatcher stopped");
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        _logger.LogDebug("--- Starting poll cycle ---");

        // 1. Fetch new error logs from Elasticsearch
        var logs = await _esPoller.FetchNewErrorsAsync(ct);
        if (logs.Count == 0)
        {
            _logger.LogDebug("No new error logs found");
            return;
        }

        // 2. Spike detection
        var spikes = _esPoller.DetectSpikes(logs);
        if (spikes.Count > 0)
            _logger.LogWarning("{Count} error spike(s) detected this cycle", spikes.Count);

        // 3. AI classification
        var classified = await _classifier.ClassifyAsync(logs, spikes, ct);
        _logger.LogInformation(
            "{Total} logs → {Actionable} actionable errors after AI classification",
            logs.Count, classified.Count);

        // 4–6. Process each classified error in its own scope
        foreach (var error in classified)
        {
            await ProcessErrorAsync(error, ct);
        }
    }

    private async Task ProcessErrorAsync(ClassifiedError error, CancellationToken ct)
    {
        // Deduplicate
        if (_dedup.IsKnownError(error.ErrorFingerprint))
        {
            _logger.LogDebug("Skipping known error {Fingerprint}: {Title}",
                error.ErrorFingerprint, error.Title);
            return;
        }

        // Rate limit guard
        if (!_dedup.IsWithinRateLimit(_githubSettings.MaxIssuesPerHour))
        {
            _logger.LogWarning(
                "Rate limit reached ({Max}/hr) — skipping issue creation for: {Title}",
                _githubSettings.MaxIssuesPerHour, error.Title);
            return;
        }

        var spikeTag = error.IsSpike ? $" [SPIKE x{error.SpikeCount}]" : string.Empty;
        _logger.LogInformation("Creating GitHub issue{Spike}: {Title}", spikeTag, error.Title);

        // Use a scope so Scoped services (GitHubService, NotificationService) are safe
        using var scope   = _scopeFactory.CreateScope();
        var github        = scope.ServiceProvider.GetRequiredService<GitHubService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var issue = await github.CreateIssueAsync(error, ct);
        if (issue == null) return;

        _dedup.TrackIssue(new TrackedIssue
        {
            ErrorFingerprint  = error.ErrorFingerprint,
            GitHubIssueNumber = issue.Number,
            Status            = "open",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        });
        _dedup.RecordIssueCreated();

        await notifications.NotifyIssueCreatedAsync(error, issue, ct);

        _logger.LogInformation("Issue #{Number} created and assigned to Copilot: {Url}",
            issue.Number, issue.HtmlUrl);
    }
}
