using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogWatcher.Services;

/// <summary>
/// Interacts with GitHub REST API:
///  - Creates issues assigned to copilot-swe-agent[bot]
///  - Finds and polls Copilot-created PRs
/// </summary>
public class GitHubService
{
    private readonly GitHubSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<GitHubService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public GitHubService(
        IOptions<AppSettings> options,
        IHttpClientFactory httpFactory,
        ILogger<GitHubService> logger)
    {
        _settings = options.Value.GitHub;
        _logger   = logger;
        _http     = httpFactory.CreateClient("github");
    }

    // ── Issue creation ────────────────────────────────────────────────────────

    public async Task<GitHubIssueResponse?> CreateIssueAsync(
        ClassifiedError error, CancellationToken ct)
    {
        var log = error.OriginalLog;

        // Context fields table
        var contextTable = BuildContextTable(log);

        // Spike / occurrence banners
        var spikeNote       = error.IsSpike
            ? $"> 🚨 **SPIKE DETECTED** — this error occurred **{error.SpikeCount} times** in a short window."
            : string.Empty;
        var occurrencesNote = error.GroupedLogs.Count > 1
            ? $"> ⚠️ This error appeared **{error.GroupedLogs.Count} times** in the last poll cycle."
            : string.Empty;

        // Reproduction hints
        var reproSection = error.ReproductionHints.Count > 0
            ? "### Reproduction hints\n" +
              string.Join("\n", error.ReproductionHints.Select(h => $"- {h}"))
            : string.Empty;

        // Stack trace section (kept separate to avoid nested interpolation issues)
        var stackSection = !string.IsNullOrEmpty(log.Exception)
            ? "### Stack trace\n```\n" + log.Exception + "\n```"
            : string.Empty;

        var body = string.Join("\n\n", new[]
        {
            "## 🤖 Auto-detected by LogWatcher",
            spikeNote,
            occurrencesNote,
            error.Summary,
            "---",
            "### Classification",
            "| Field | Value |",
            "|-------|-------|",
            $"| **Severity** | `{error.Severity}` |",
            $"| **Root cause** | `{error.RootCauseCategory ?? "Unknown"}` |",
            $"| **Suggested area** | `{error.SuggestedArea ?? "unknown"}` |",
            $"| **Confidence** | {error.ConfidenceScore}% |",
            $"| **Service** | `{log.Service ?? "unknown"}` |",
            $"| **Timestamp** | `{log.Timestamp:O}` |",
            $"| **Fingerprint** | `{error.ErrorFingerprint}` |",
            reproSection,
            contextTable,
            "---",
            "### Error message",
            "```",
            log.Message,
            "```",
            stackSection,
            "---",
            "*Created automatically by LogWatcher. Review all changes before merging.*"
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var payload = new
        {
            title = $"[{error.Severity.ToUpperInvariant()}] {error.Title}",
            body,
            labels    = error.SuggestedLabels.Count > 0
                ? error.SuggestedLabels
                : new List<string> { "bug", "auto-detected" },
            assignees = new[] { "copilot-swe-agent[bot]" }
        };

        var url = $"https://api.github.com/repos/{_settings.Owner}/{_settings.Repo}/issues";

        try
        {
            var content  = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("GitHub issue creation failed {Status}: {Error}",
                    (int)response.StatusCode, err);
                return null;
            }

            var json   = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GitHubIssueResponse>(json, JsonOpts);
            _logger.LogInformation("Created GitHub issue #{Number}: {Title}",
                result?.Number, result?.Title);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating GitHub issue");
            return null;
        }
    }

    // ── PR polling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks for a Copilot-created PR in the repo.
    /// Copilot names its branches "copilot/...".
    /// </summary>
    public async Task<GitHubPrResponse?> FindCopilotPrAsync(
        int issueNumber, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_settings.Owner}/{_settings.Repo}" +
                  "/pulls?state=all&per_page=30&sort=created&direction=desc";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            var prs  = JsonSerializer.Deserialize<List<GitHubPrResponse>>(json, JsonOpts)
                       ?? new List<GitHubPrResponse>();

            return prs.FirstOrDefault(pr =>
                pr.Head.Ref.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching PRs for issue #{Number}", issueNumber);
            return null;
        }
    }

    public async Task<GitHubPrResponse?> GetPrAsync(int prNumber, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_settings.Owner}/{_settings.Repo}" +
                  $"/pulls/{prNumber}";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<GitHubPrResponse>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching PR #{Number}", prNumber);
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildContextTable(LogEntry log)
    {
        if (log.ContextFields.Count == 0) return string.Empty;

        var rows = log.ContextFields
            .Select(kv => $"| **{kv.Key}** | `{kv.Value}` |");

        return "### Request context\n" +
               "| Field | Value |\n" +
               "|-------|-------|\n" +
               string.Join("\n", rows);
    }
}
