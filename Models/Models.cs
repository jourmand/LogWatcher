namespace LogWatcher.Models;

public class LogEntry
{
    public string Id { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? Service { get; set; }
    public Dictionary<string, string> ContextFields { get; set; } = new();
    public Dictionary<string, object?> Fields { get; set; } = new();
}

public class ClassifiedError
{
    public LogEntry OriginalLog { get; set; } = new();
    public string Severity { get; set; } = "";
    public bool IsFixableByCode { get; set; }
    public int ConfidenceScore { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? RootCauseCategory { get; set; }
    public string? SuggestedArea { get; set; }
    public List<string> ReproductionHints { get; set; } = [];
    public List<string> SuggestedLabels { get; set; } = [];
    public bool IsSpike { get; set; }
    public int SpikeCount { get; set; }
    public List<LogEntry> GroupedLogs { get; set; } = [];
    public string ErrorFingerprint { get; set; } = "";
}

public class SpikeEvent
{
    public string Fingerprint { get; set; } = "";
    public int Count { get; set; }
    public LogEntry RepresentativeLog { get; set; } = new();
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

// record so PrPollerWorker can use "with" expression to clone + override one field
public record TrackedIssue
{
    public long Id { get; init; }
    public string ErrorFingerprint { get; init; } = "";
    public int GitHubIssueNumber { get; init; }
    public string? GitHubPrUrl { get; init; }
    public string Status { get; init; } = "open";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public class GitHubIssueResponse
{
    public int Number { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
}

public class GitHubPrResponse
{
    public int Number { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Draft { get; set; }
    public string State { get; set; } = "";
    public GitHubBranch Head { get; set; } = new();
}

public class GitHubBranch
{
    public string Ref { get; set; } = "";
}
