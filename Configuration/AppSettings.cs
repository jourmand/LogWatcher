namespace LogWatcher.Configuration;

public class AppSettings
{
    public ElasticsearchSettings Elasticsearch { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public FreshServiceSettings FreshService { get; set; } = new();
    public AnthropicSettings Anthropic { get; set; } = new();
    public WatcherSettings Watcher { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ClassifierSettings Classifier { get; set; } = new();

    // NEW: controls whether to use MCP transport or direct ES client
    public McpSettings Mcp { get; set; } = new();
}

public class FreshServiceSettings
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 120;
    public int RecentClosedLookbackDays { get; set; } = 30;
    public int InitialNewTicketLookbackMinutes { get; set; } = 30;
    public int MaxPages { get; set; } = 4;
    public int PerPage { get; set; } = 50;
    public double SimilarityThreshold { get; set; } = 0.35;
}

public class McpSettings
{
    /// <summary>
    /// true  = use Elasticsearch MCP server (npx @elastic/mcp-server-elasticsearch)
    /// false = use direct Elastic.Clients.Elasticsearch (original behaviour)
    /// </summary>
    public bool UseElasticsearchMcp { get; set; } = false;

    /// <summary>
    /// How the MCP server process is launched.
    /// "npx"    → npx -y @elastic/mcp-server-elasticsearch  (requires Node.js)
    /// "docker" → docker run … docker.elastic.co/mcp/elasticsearch stdio
    /// </summary>
    public string Transport { get; set; } = "npx";   // "npx" | "docker"

    /// <summary>ES_API_KEY passed to the MCP server process.</summary>
    public string EsApiKey { get; set; } = "";

    /// <summary>Timeout in seconds for a single MCP search call.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

public class ElasticsearchSettings
{
    public string Url { get; set; } = "http://localhost:9200";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string IndexPattern { get; set; } = "demoapp-logs-*";
    public string LevelField { get; set; } = "level";
    public string MessageField { get; set; } = "message";
    public string ExceptionField { get; set; } = "exception";
    public string ServiceField { get; set; } = "service";
    public string TimestampField { get; set; } = "@timestamp";
    public List<string> ExtraContextFields { get; set; } = ["TraceId", "RequestPath", "UserId", "Environment"];
    public List<string> IncludeServices { get; set; } = [];
    public List<string> ExcludeServices { get; set; } = [];
    public List<string> ErrorLevels { get; set; } = ["error", "critical", "fatal", "ERROR", "CRITICAL", "FATAL"];
    public List<string> ExcludeMessagePatterns { get; set; } =
        ["health check", "heartbeat", "Connection reset by peer"];
    public List<string> ExcludeMessageRegex { get; set; } = [];
    public bool EnableSpikeDetection { get; set; } = true;
    public int SpikeThreshold { get; set; } = 10;
    public int SpikeWindowMinutes { get; set; } = 5;
    public int MaxResultsPerPoll { get; set; } = 200;
    public bool UseScrollApi { get; set; } = false;
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
    // Allowed values: "mcp" or "direct" (aka "pulling").
    // When empty, falls back to Mcp.UseElasticsearchMcp.
    public string ElasticsearchSource { get; set; } = "direct";
    public string MinSeverity { get; set; } = "error";
    public int DeduplicationWindowHours { get; set; } = 24;
}

public class ClassifierSettings
{
    public List<string> TechStack { get; set; } = [];
    public string? CodebaseContext { get; set; }
    public Dictionary<string, List<string>> SeverityLabels { get; set; } = new()
    {
        ["critical"] = ["bug", "critical", "auto-detected"],
        ["error"]    = ["bug", "auto-detected"],
        ["warning"]  = ["enhancement", "auto-detected"]
    };
    public bool EnableRootCauseHints { get; set; } = true;
    public bool GroupSimilarErrors { get; set; } = true;
    public int MinConfidenceScore { get; set; } = 60;
}

public class NotificationSettings
{
    public string SlackWebhookUrl { get; set; } = "";
}
