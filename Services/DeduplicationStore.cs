using Dapper;
using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LogWatcher.Services;

/// <summary>
/// Persists error fingerprints and tracked issues in a local SQLite database.
/// Survives restarts — no duplicate GitHub issues after a crash or redeploy.
/// </summary>
public class DeduplicationStore
{
    private readonly string _connectionString;
    private readonly WatcherSettings _settings;
    private readonly ILogger<DeduplicationStore> _logger;

    public DeduplicationStore(IOptions<AppSettings> options, ILogger<DeduplicationStore> logger)
    {
        _settings = options.Value.Watcher;
        _logger   = logger;

        var dbPath = Path.Combine(AppContext.BaseDirectory, "logwatcher.db");
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "CREATE TABLE IF NOT EXISTS tracked_issues (" +
            "  id                  INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  error_fingerprint   TEXT NOT NULL UNIQUE," +
            "  github_issue_number INTEGER NOT NULL," +
            "  github_pr_url       TEXT," +
            "  status              TEXT NOT NULL DEFAULT 'open'," +
            "  created_at          TEXT NOT NULL," +
            "  updated_at          TEXT NOT NULL" +
            ");" +
            "CREATE INDEX IF NOT EXISTS idx_fingerprint ON tracked_issues(error_fingerprint);" +
            "CREATE INDEX IF NOT EXISTS idx_status      ON tracked_issues(status);" +
            "CREATE TABLE IF NOT EXISTS rate_limit_log (" +
            "  id         INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  created_at TEXT NOT NULL" +
            ");");

        _logger.LogInformation("SQLite database ready: {Path}", _connectionString);
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    public bool IsKnownError(string fingerprint)
    {
        using var conn   = new SqliteConnection(_connectionString);
        var cutoff       = DateTime.UtcNow.AddHours(-_settings.DeduplicationWindowHours).ToString("O");
        var count        = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM tracked_issues " +
            "WHERE error_fingerprint = @fp AND created_at > @cutoff",
            new { fp = fingerprint, cutoff });
        return count > 0;
    }

    public void TrackIssue(TrackedIssue issue)
    {
        using var conn = new SqliteConnection(_connectionString);
        var now        = DateTime.UtcNow.ToString("O");
        conn.Execute(
            "INSERT INTO tracked_issues " +
            "  (error_fingerprint, github_issue_number, status, created_at, updated_at) " +
            "VALUES (@fp, @issueNum, @status, @now, @now) " +
            "ON CONFLICT(error_fingerprint) DO NOTHING",
            new
            {
                fp       = issue.ErrorFingerprint,
                issueNum = issue.GitHubIssueNumber,
                status   = issue.Status,
                now
            });
    }

    public void UpdateIssueStatus(string fingerprint, string status, string? prUrl = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "UPDATE tracked_issues " +
            "SET status = @status, " +
            "    github_pr_url = COALESCE(@prUrl, github_pr_url), " +
            "    updated_at = @now " +
            "WHERE error_fingerprint = @fp",
            new { status, prUrl, fp = fingerprint, now = DateTime.UtcNow.ToString("O") });
    }

    public List<TrackedIssue> GetOpenIssues()
    {
        using var conn = new SqliteConnection(_connectionString);
        return conn.Query<TrackedIssue>(
            "SELECT id                  AS Id," +
            "       error_fingerprint   AS ErrorFingerprint," +
            "       github_issue_number AS GitHubIssueNumber," +
            "       github_pr_url       AS GitHubPrUrl," +
            "       status              AS Status," +
            "       created_at          AS CreatedAt," +
            "       updated_at          AS UpdatedAt " +
            "FROM tracked_issues " +
            "WHERE status IN ('open', 'pr_created')")
            .ToList();
    }

    // ── Rate limiting ─────────────────────────────────────────────────────────

    public bool IsWithinRateLimit(int maxPerHour)
    {
        using var conn = new SqliteConnection(_connectionString);
        var cutoff     = DateTime.UtcNow.AddHours(-1).ToString("O");
        var count      = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM rate_limit_log WHERE created_at > @cutoff",
            new { cutoff });
        return count < maxPerHour;
    }

    public void RecordIssueCreated()
    {
        using var conn = new SqliteConnection(_connectionString);
        var now        = DateTime.UtcNow.ToString("O");
        conn.Execute(
            "INSERT INTO rate_limit_log (created_at) VALUES (@now)", new { now });
        // Prune entries older than 2 hours
        conn.Execute(
            "DELETE FROM rate_limit_log WHERE created_at < @cutoff",
            new { cutoff = DateTime.UtcNow.AddHours(-2).ToString("O") });
    }
}
