using Dapper;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;
using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogWatcher.Services;

/// <summary>
/// Polls Elasticsearch for new error logs.
/// Features: service filters, message exclusions, extra context fields,
/// spike detection, persistent checkpoint, API-key + basic auth support.
/// </summary>
public class ElasticsearchPoller
{
    private readonly ElasticsearchSettings _cfg;
    private readonly ILogger<ElasticsearchPoller> _logger;
    private readonly ElasticsearchClient _client;
    private readonly string _connectionString;
    private readonly List<Regex> _excludeRegexes;

    public ElasticsearchPoller(IOptions<AppSettings> options, ILogger<ElasticsearchPoller> logger)
    {
        _cfg = options.Value.Elasticsearch;
        _logger = logger;

        var dbPath = Path.Combine(AppContext.BaseDirectory, "logwatcher.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureCheckpointTable();

        _client = BuildClient();

        _excludeRegexes = _cfg.ExcludeMessageRegex
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<List<LogEntry>> FetchNewErrorsAsync(CancellationToken ct)
    {
        var from = LoadCheckpoint();
        var to   = DateTime.UtcNow;

        _logger.LogDebug("ES poll window: {From:O} to {To:O}", from, to);

        var raw = await QueryElasticsearchAsync(from, to, ct);
        if (raw.Count == 0) return [];

        SaveCheckpoint(to);

        var filtered = ApplyClientSideFilters(raw);

        _logger.LogInformation(
            "ES poll: {Raw} raw logs, {Filtered} after exclusion filters",
            raw.Count, filtered.Count);

        return filtered;
    }

    public List<SpikeEvent> DetectSpikes(List<LogEntry> batch)
    {
        if (!_cfg.EnableSpikeDetection || batch.Count == 0)
            return [];

        var spikes = batch
            .GroupBy(ErrorClassifier.ComputeFingerprint)
            .Where(g => g.Count() >= _cfg.SpikeThreshold)
            .Select(g => new SpikeEvent
            {
                Fingerprint       = g.Key,
                Count             = g.Count(),
                RepresentativeLog = g.OrderByDescending(l => l.Timestamp).First(),
                FirstSeen         = g.Min(l => l.Timestamp),
                LastSeen          = g.Max(l => l.Timestamp)
            })
            .ToList();

        if (spikes.Count > 0)
            _logger.LogWarning("Spike detection: {Count} spike(s) in current batch", spikes.Count);

        return spikes;
    }

    // ── ES Query ──────────────────────────────────────────────────────────────

    private async Task<List<LogEntry>> QueryElasticsearchAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            var luceneQuery = BuildLuceneQuery(from, to);

            var response = await _client.SearchAsync<JsonElement>(s =>
            {
                s.Indices(_cfg.IndexPattern)
                 .Size(_cfg.MaxResultsPerPoll)
                 .Sort(so => so.Field(new Field(_cfg.TimestampField), f => f.Order(SortOrder.Asc)))
                 .Query(q => q.QueryString(qs => qs.Query(luceneQuery)));
            }, ct);

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("ES query returned invalid response: {Reason}",
                    response.ElasticsearchServerError?.Error?.Reason);
                return [];
            }

            return response.Hits
                .Where(h => h.Source.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                .Select(h => MapToLogEntry(h.Id ?? string.Empty, h.Source))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception querying Elasticsearch");
            return [];
        }
    }

    private string BuildLuceneQuery(DateTime from, DateTime to)
    {
        var clauses = new List<string>
        {
            $"{_cfg.TimestampField}:[{QuoteForLucene(from.ToString("O"))} TO {QuoteForLucene(to.ToString("O"))}]"
        };

        var levels = _cfg.ErrorLevels
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => QuoteForLucene(v.Trim()))
            .Distinct()
            .ToList();

        if (levels.Count > 0)
            clauses.Add($"{_cfg.LevelField}:({string.Join(" OR ", levels)})");

        var includeServices = _cfg.IncludeServices
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => QuoteForLucene(v.Trim()))
            .Distinct()
            .ToList();

        if (includeServices.Count > 0)
            clauses.Add($"{_cfg.ServiceField}:({string.Join(" OR ", includeServices)})");

        if (!string.IsNullOrWhiteSpace(_cfg.AdditionalQueryFilter))
            clauses.Add($"({_cfg.AdditionalQueryFilter})");

        var excludeServices = _cfg.ExcludeServices
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => QuoteForLucene(v.Trim()))
            .Distinct()
            .ToList();

        if (excludeServices.Count > 0)
            clauses.Add($"NOT {_cfg.ServiceField}:({string.Join(" OR ", excludeServices)})");

        return string.Join(" AND ", clauses);
    }

    private static string QuoteForLucene(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // ── Client-side filters ───────────────────────────────────────────────────

    private List<LogEntry> ApplyClientSideFilters(List<LogEntry> entries) =>
        entries.Where(e => !IsExcluded(e)).ToList();

    private bool IsExcluded(LogEntry entry)
    {
        var msg = entry.Message;

        foreach (var pattern in _cfg.ExcludeMessagePatterns)
        {
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Excluded by substring pattern '{Pattern}'", pattern);
                return true;
            }
        }

        foreach (var regex in _excludeRegexes)
        {
            if (regex.IsMatch(msg))
            {
                _logger.LogDebug("Excluded by regex '{Pattern}'", regex);
                return true;
            }
        }

        return false;
    }

    // ── Document mapping ──────────────────────────────────────────────────────

    private LogEntry MapToLogEntry(string id, JsonElement doc)
    {
        // Supports dot-notation with arrays: "exceptions.0.StackTraceString"
        string GetString(string fieldPath)
        {
            var parts = fieldPath.Split('.');
            var current = doc;
            foreach (var part in parts)
            {
                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (!current.TryGetProperty(part, out var next)) return string.Empty;
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index))
                {
                    if (index < 0 || index >= current.GetArrayLength()) return string.Empty;
                    current = current[index];
                }
                else
                {
                    return string.Empty;
                }
            }

            return current.ValueKind == JsonValueKind.String
                ? current.GetString() ?? string.Empty
                : current.ToString();
        }

        static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        string BuildExceptionFallback()
        {
            var type = GetString("exceptions.0.ClassName");
            var message = GetString("exceptions.0.Message");
            var stack = GetString("exceptions.0.StackTraceString");

            if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(stack))
                return string.Empty;

            return string.Join("\n", new[] { type, message, stack }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        DateTime GetTimestamp()
        {
            var raw = GetString(_cfg.TimestampField);
            return DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
        }

        var contextFields = new Dictionary<string, string>();
        foreach (var field in _cfg.ExtraContextFields)
        {
            var value = GetString(field);
            if (!string.IsNullOrEmpty(value))
                contextFields[field] = value;
        }

        var fields = new Dictionary<string, object?>();
        foreach (var prop in doc.EnumerateObject())
            fields[prop.Name] = prop.Value.Clone();

        var exception = FirstNonEmpty(
            GetString(_cfg.ExceptionField),
            GetString("exceptions.0.StackTraceString"),
            GetString("exceptions.0.Message"),
            BuildExceptionFallback());

        var service = FirstNonEmpty(
            GetString(_cfg.ServiceField),
            GetString("service"),
            GetString("fields.service"),
            GetString("fields.Service"));

        return new LogEntry
        {
            Id            = id,
            Timestamp     = GetTimestamp(),
            Level         = GetString(_cfg.LevelField),
            Message       = GetString(_cfg.MessageField),
            Exception     = exception,
            Service       = service,
            ContextFields = contextFields,
            Fields        = fields
        };
    }

    // ── Checkpoint ────────────────────────────────────────────────────────────

    private void EnsureCheckpointTable()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "CREATE TABLE IF NOT EXISTS poll_checkpoint (" +
            "  id          INTEGER PRIMARY KEY CHECK (id = 1)," +
            "  last_polled TEXT NOT NULL" +
            ");");
    }

    private DateTime LoadCheckpoint()
    {
        using var conn = new SqliteConnection(_connectionString);
        var stored = conn.QueryFirstOrDefault<string>(
            "SELECT last_polled FROM poll_checkpoint WHERE id = 1");
        if (stored != null && DateTime.TryParse(stored, out var dt))
        {
            _logger.LogDebug("Loaded checkpoint: {Checkpoint}", stored);
            return dt;
        }
        return DateTime.UtcNow.AddMinutes(-5);
    }

    private void SaveCheckpoint(DateTime value)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "INSERT INTO poll_checkpoint (id, last_polled) VALUES (1, @v) " +
            "ON CONFLICT(id) DO UPDATE SET last_polled = @v",
            new { v = value.ToString("O") });
    }

    // ── Client factory ────────────────────────────────────────────────────────

    private ElasticsearchClient BuildClient()
    {
        var nodeUri = new Uri(_cfg.Url);
        ElasticsearchClientSettings settings;

        if (!string.IsNullOrEmpty(_cfg.ApiKey))
        {
            settings = new ElasticsearchClientSettings(nodeUri)
                .Authentication(new ApiKey(_cfg.ApiKey));
        }
        else if (!string.IsNullOrEmpty(_cfg.Username))
        {
            settings = new ElasticsearchClientSettings(nodeUri)
                .Authentication(new BasicAuthentication(_cfg.Username, _cfg.Password));
        }
        else
        {
            settings = new ElasticsearchClientSettings(nodeUri);
        }

        // EnableDebugMode returns the settings instance — must reassign
        settings = (ElasticsearchClientSettings)settings.EnableDebugMode(d =>
        {
            if (d.HttpStatusCode >= 400)
                _logger.LogDebug("ES [{Status}] {Uri}", d.HttpStatusCode, d.Uri);
        });

        return new ElasticsearchClient(settings);
    }
}
