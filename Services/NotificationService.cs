using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace LogWatcher.Services;

public class NotificationService
{
    private readonly NotificationSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IOptions<AppSettings> options,
        IHttpClientFactory httpFactory,
        ILogger<NotificationService> logger)
    {
        _settings = options.Value.Notifications;
        _http     = httpFactory.CreateClient();   // default unnamed client
        _logger   = logger;
    }

    public async Task NotifyIssueCreatedAsync(
        ClassifiedError error, GitHubIssueResponse issue, CancellationToken ct)
    {
        var emoji = error.Severity switch
        {
            "critical" => "🚨",
            "error"    => "🔴",
            _          => "⚠️"
        };

        var payload = new
        {
            text = $"{emoji} *New bug issue created and assigned to Copilot*",
            attachments = new[]
            {
                new
                {
                    color  = error.Severity == "critical" ? "#FF0000" : "#FF6600",
                    fields = new[]
                    {
                        new { title = "Issue",    value = $"<{issue.HtmlUrl}|#{issue.Number}: {issue.Title}>", @short = false },
                        new { title = "Service",  value = error.OriginalLog.Service ?? "unknown",              @short = true  },
                        new { title = "Severity", value = error.Severity,                                      @short = true  }
                    }
                }
            }
        };

        await SendSlackAsync(payload, ct);
    }

    public async Task NotifyPrReadyAsync(TrackedIssue trackedIssue, CancellationToken ct)
    {
        var payload = new
        {
            text = "✅ *Copilot finished — PR is ready for your review*",
            attachments = new[]
            {
                new
                {
                    color  = "#36A64F",
                    fields = new[]
                    {
                        new { title = "Pull Request",  value = trackedIssue.GitHubPrUrl ?? "unknown",            @short = false },
                        new { title = "GitHub Issue",  value = $"#{trackedIssue.GitHubIssueNumber}",             @short = true  }
                    }
                }
            }
        };

        await SendSlackAsync(payload, ct);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task SendSlackAsync(object payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.SlackWebhookUrl))
        {
            _logger.LogDebug("Slack webhook not configured — skipping notification");
            return;
        }

        try
        {
            var json     = JsonSerializer.Serialize(payload);
            var content  = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_settings.SlackWebhookUrl, content, ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Slack notification failed with status {Status}",
                    (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception sending Slack notification");
        }
    }
}
