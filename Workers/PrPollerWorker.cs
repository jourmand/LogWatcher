using LogWatcher.Configuration;
using LogWatcher.Services;
using Microsoft.Extensions.Options;

namespace LogWatcher.Workers;

/// <summary>
/// Always-running background service that polls GitHub for open Copilot PRs.
/// When a PR transitions from draft → ready for review, fires a Slack notification.
/// Uses IServiceScopeFactory to safely resolve Scoped services from a Singleton worker.
/// </summary>
public class PrPollerWorker : BackgroundService
{
    private readonly DeduplicationStore  _dedup;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WatcherSettings     _settings;
    private readonly ILogger<PrPollerWorker> _logger;

    // Remembers PR numbers we've already located, to skip redundant searches
    private readonly Dictionary<string, int> _issueToPrNumber = new();

    public PrPollerWorker(
        DeduplicationStore dedup,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> options,
        ILogger<PrPollerWorker> logger)
    {
        _dedup        = dedup;
        _scopeFactory = scopeFactory;
        _settings     = options.Value.Watcher;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PR Poller started. Poll interval: {Interval}s",
            _settings.PrPollIntervalSeconds);

        // Stagger start so it doesn't race with LogWatcherWorker on first boot
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOpenIssuesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PR poller cycle");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.PrPollIntervalSeconds), stoppingToken);
        }
    }

    private async Task PollOpenIssuesAsync(CancellationToken ct)
    {
        var openIssues = _dedup.GetOpenIssues();
        if (openIssues.Count == 0) return;

        _logger.LogDebug("Polling {Count} open issue(s) for PR status", openIssues.Count);

        // Create one scope per cycle — both services are used for the whole cycle
        using var scope   = _scopeFactory.CreateScope();
        var github        = scope.ServiceProvider.GetRequiredService<GitHubService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        foreach (var tracked in openIssues)
        {
            try
            {
                int? prNumber = null;

                if (_issueToPrNumber.TryGetValue(tracked.ErrorFingerprint, out var knownPr))
                {
                    prNumber = knownPr;
                }
                else
                {
                    var found = await github.FindCopilotPrAsync(tracked.GitHubIssueNumber, ct);
                    if (found != null)
                    {
                        prNumber = found.Number;
                        _issueToPrNumber[tracked.ErrorFingerprint] = found.Number;
                        _dedup.UpdateIssueStatus(
                            tracked.ErrorFingerprint, "pr_created", found.HtmlUrl);
                        _logger.LogInformation(
                            "Copilot opened PR #{PrNumber} for issue #{IssueNumber}",
                            found.Number, tracked.GitHubIssueNumber);
                    }
                }

                if (prNumber == null) continue;

                var pr = await github.GetPrAsync(prNumber.Value, ct);
                if (pr == null) continue;

                if (pr.State == "closed")
                {
                    _dedup.UpdateIssueStatus(tracked.ErrorFingerprint, "done");
                    _issueToPrNumber.Remove(tracked.ErrorFingerprint);
                    _logger.LogInformation("PR #{PrNumber} closed/merged", prNumber);
                    continue;
                }

                // Copilot marks the PR ready for review by removing the draft flag
                if (!pr.Draft && tracked.Status == "pr_created")
                {
                    _dedup.UpdateIssueStatus(
                        tracked.ErrorFingerprint, "pr_ready", pr.HtmlUrl);
                    _logger.LogInformation("PR #{PrNumber} ready for review: {Url}",
                        prNumber, pr.HtmlUrl);

                    // "with" expression works because TrackedIssue is a record
                    await notifications.NotifyPrReadyAsync(
                        tracked with { GitHubPrUrl = pr.HtmlUrl }, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error checking PR status for issue #{IssueNumber}",
                    tracked.GitHubIssueNumber);
            }

            // Respect GitHub's secondary rate limit between calls
            await Task.Delay(500, ct);
        }
    }
}
