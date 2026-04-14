namespace LogWatcher.Configuration;

public class AppSettings
{
    public ElasticsearchSettings Elasticsearch { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public AnthropicSettings Anthropic { get; set; } = new();
    public WatcherSettings Watcher { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ClassifierSettings Classifier { get; set; } = new();
}

public class ElasticsearchSettings
{
    public string Url { get; set; } = "http://localhost:9200";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiKey { get; set; } = "";          // alternative to basic auth

    public string IndexPattern { get; set; } = "logs-*";

    // ── Field name mapping ────────────────────────────────────────────────────
    // Adjust these to match your actual ES document schema
    public string LevelField { get; set; } = "level";
    public string MessageField { get; set; } = "message";
    public string ExceptionField { get; set; } = "exception";
    public string ServiceField { get; set; } = "service";
    public string TimestampField { get; set; } = "@timestamp";
    // Optional extra context fields to include in the GitHub issue body
    public List<string> ExtraContextFields { get; set; } = new()
        { "TraceId", "RequestPath", "UserId", "Environment" };

    // ── Scope filters ─────────────────────────────────────────────────────────
    // Only watch logs from these services (empty = watch all)
    public List<string> IncludeServices { get; set; } = new();
    // Never create issues for logs from these services
    public List<string> ExcludeServices { get; set; } = new();
    // Only watch these log levels (default covers the standard error levels)
    public List<string> ErrorLevels { get; set; } = new()
        { "error", "critical", "fatal", "ERROR", "CRITICAL", "FATAL" };

    // ── Message filters ───────────────────────────────────────────────────────
    // Substring patterns — logs matching ANY of these are silently ignored
    // Useful for known noisy errors you don't want issues for
    public List<string> ExcludeMessagePatterns { get; set; } = new()
        { "health check", "heartbeat", "Connection reset by peer" };
    // Regex patterns applied to the message field (case-insensitive)
    public List<string> ExcludeMessageRegex { get; set; } = new();

    // ── Spike detection ───────────────────────────────────────────────────────
    // Detect sudden bursts: if the same error appears >= threshold times
    // within the spike window, treat it as a spike regardless of dedup
    public bool EnableSpikeDetection { get; set; } = true;
    public int SpikeThreshold { get; set; } = 10;            // occurrences
    public int SpikeWindowMinutes { get; set; } = 5;         // within this window

    // ── Query tuning ──────────────────────────────────────────────────────────
    // Max log entries fetched per poll cycle
    public int MaxResultsPerPoll { get; set; } = 200;
    // Use ES scroll API for large result sets (> 500 logs expected per cycle)
    public bool UseScrollApi { get; set; } = false;
    // Additional raw Lucene filter query applied to every search (advanced)
    // Example: "environment:production AND NOT kubernetes.namespace:staging"
    public string? AdditionalQueryFilter { get; set; }
}

public class GitHubSettings
{
    public string PersonalAccessToken { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public int MaxIssuesPerHour { get; set; } = 5;
}

public class AnthropicSettings
{
    public string ApiKey { get; set; } = "";
}

public class WatcherSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int PrPollIntervalSeconds { get; set; } = 30;
    public string MinSeverity { get; set; } = "error";
    public int DeduplicationWindowHours { get; set; } = 24;
}

public class ClassifierSettings
{
    // Your tech stack — helps Claude give better root cause analysis
    // e.g. ["ASP.NET Core 8", "Entity Framework Core", "RabbitMQ", "Redis"]
    public List<string> TechStack { get; set; } = new();

    // Hint Claude about common patterns in YOUR codebase
    // e.g. "Our services use Result<T> pattern, never throw for business errors"
    public string? CodebaseContext { get; set; }

    // Labels to apply per severity on GitHub issues
    public Dictionary<string, List<string>> SeverityLabels { get; set; } = new()
    {
        ["critical"] = new() { "bug", "critical", "auto-detected" },
        ["error"]    = new() { "bug", "auto-detected" },
        ["warning"]  = new() { "enhancement", "auto-detected" }
    };

    // If true, Claude will try to suggest which file/class is likely responsible
    public bool EnableRootCauseHints { get; set; } = true;

    // If true, group similar errors into one issue instead of separate ones
    public bool GroupSimilarErrors { get; set; } = true;

    // Minimum confidence score (0-100) Claude must report to create an issue
    public int MinConfidenceScore { get; set; } = 60;
}

public class NotificationSettings
{
    public string SlackWebhookUrl { get; set; } = "";
}
