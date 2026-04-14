using LogWatcher.Configuration;
using LogWatcher.Models;
using LogWatcher.Services;
using Microsoft.Extensions.Options;

namespace LogWatcher.Workers;

/// <summary>
/// Always-running background service that:
/// 1. Fetches new error logs — via MCP (if enabled) or direct ES client
/// 2. Detects error spikes
/// 3. Classifies errors with Claude AI
/// 4. Deduplicates and rate-limits
/// 5. Creates GitHub issues assigned to Copilot agent
/// 6. Sends Slack notifications
/// </summary>
public class LogWatcherWorker : BackgroundService
{
    // One of these is active depending on Mcp:UseElasticsearchMcp
    private readonly McpElasticsearchService? _mcpPoller;
    private readonly ElasticsearchPoller?     _directPoller;

    private readonly ErrorClassifier          _classifier;
    private readonly DeduplicationStore       _dedup;
    private readonly IServiceScopeFactory     _scopeFactory;
    private readonly WatcherSettings          _watcherSettings;
    private readonly GitHubSettings           _githubSettings;
    private readonly ILogger<LogWatcherWorker> _logger;

    public LogWatcherWorker(
        IServiceScopeFactory scopeFactory,
        ErrorClassifier classifier,
        DeduplicationStore dedup,
        IOptions<AppSettings> options,
        ILogger<LogWatcherWorker> logger,
        // Both are registered; only the active one is used
        McpElasticsearchService mcpPoller,
        ElasticsearchPoller directPoller)
    {
        _scopeFactory    = scopeFactory;
        _classifier      = classifier;
        _dedup           = dedup;
        _watcherSettings = options.Value.Watcher;
        _githubSettings  = options.Value.GitHub;
        _logger          = logger;

        var source = _watcherSettings.ElasticsearchSource?.Trim().ToLowerInvariant();
        var useMcp = source switch
        {
            "mcp" => true,
            "direct" => false,
            "pulling" => false,
            null or "" => options.Value.Mcp.UseElasticsearchMcp,
            _ => options.Value.Mcp.UseElasticsearchMcp
        };

        if (source is not null && source is not "" and not "mcp" and not "direct" and not "pulling")
        {
            _logger.LogWarning(
                "Unknown AppSettings:Watcher:ElasticsearchSource value '{Value}'. Falling back to AppSettings:Mcp:UseElasticsearchMcp={Fallback}",
                _watcherSettings.ElasticsearchSource,
                options.Value.Mcp.UseElasticsearchMcp);
        }

        _mcpPoller    = useMcp ? mcpPoller    : null;
        _directPoller = useMcp ? null         : directPoller;

        _logger.LogInformation(
            "LogWatcher using {Mode} for Elasticsearch",
            useMcp ? "MCP (Elastic MCP server)" : "direct Elasticsearch client");
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

        // 1. Fetch logs — MCP path or direct path
        List<LogEntry> logs;
        List<SpikeEvent> spikes;

        if (_mcpPoller != null)
        {
            logs   = await _mcpPoller.FetchNewErrorsAsync(ct);
            spikes = _mcpPoller.DetectSpikes(logs);
        }
        else
        {
            logs   = await _directPoller!.FetchNewErrorsAsync(ct);
            spikes = _directPoller.DetectSpikes(logs);
        }

        if (logs.Count == 0)
        {
            _logger.LogDebug("No new error logs found");
            return;
        }

        if (spikes.Count > 0)
            _logger.LogWarning("{Count} error spike(s) detected this cycle", spikes.Count);

        // 2. AI classification
        var classified = await _classifier.ClassifyAsync(logs, spikes, ct);
        _logger.LogInformation(
            "{Total} logs → {Actionable} actionable errors after AI classification",
            logs.Count, classified.Count);

        // 3. Process each error
        foreach (var error in classified)
            await ProcessErrorAsync(error, ct);
    }

    private async Task ProcessErrorAsync(ClassifiedError error, CancellationToken ct)
    {
        if (_dedup.IsKnownError(error.ErrorFingerprint))
        {
            _logger.LogDebug("Skipping known error {Fingerprint}: {Title}",
                error.ErrorFingerprint, error.Title);
            return;
        }

        if (!_dedup.IsWithinRateLimit(_githubSettings.MaxIssuesPerHour))
        {
            _logger.LogWarning(
                "Rate limit reached ({Max}/hr) — skipping: {Title}",
                _githubSettings.MaxIssuesPerHour, error.Title);
            return;
        }

        var spikeTag = error.IsSpike ? $" [SPIKE x{error.SpikeCount}]" : string.Empty;
        _logger.LogInformation("Creating GitHub issue{Spike}: {Title}", spikeTag, error.Title);

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
