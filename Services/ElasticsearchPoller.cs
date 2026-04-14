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
        if (raw.Count == 0) return new List<LogEntry>();

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
            return new List<SpikeEvent>();

        var spikes = batch
            .GroupBy(l => ErrorClassifier.ComputeFingerprint(l))
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
            // Build the source fields list up front
            var sourceFields = new List<string>
            {
                _cfg.LevelField, _cfg.MessageField, _cfg.ExceptionField,
                _cfg.ServiceField, _cfg.TimestampField
            };
            sourceFields.AddRange(_cfg.ExtraContextFields);
            var fieldObjects = sourceFields
                .Distinct()
                .Select(f => new Field(f))
                .ToArray();

            var response = await _client.SearchAsync<JsonElement>(s =>
            {
                s.Indices(_cfg.IndexPattern)
                 .Size(_cfg.MaxResultsPerPoll)
                 .Sort(so => so.Field(_cfg.TimestampField, f => f.Order(SortOrder.Asc)))
                 .Source(src => src.Filter(sf => sf.Includes(inc => inc.Fields(fieldObjects))));

                s.Query(q => q.Bool(b =>
                {
                    // ── Filter clauses (must match all) ───────────────────────
                    var filterClauses = new List<Action<QueryDescriptor<JsonElement>>>();

                    // Time range
                    filterClauses.Add(f => f.Range(r => r.DateRange(dr => dr
                        .Field(_cfg.TimestampField)
                        .Gte(from.ToString("O"))
                        .Lt(to.ToString("O")))));

                    // Error levels
                    filterClauses.Add(f => f.Terms(t => t
                        .Field(_cfg.LevelField)
                        .Terms(new TermsQueryField(
                            _cfg.ErrorLevels.Select(FieldValue.String).ToArray()))));

                    // Include services (optional)
                    if (_cfg.IncludeServices.Count > 0)
                    {
                        filterClauses.Add(f => f.Terms(t => t
                            .Field(_cfg.ServiceField)
                            .Terms(new TermsQueryField(
                                _cfg.IncludeServices.Select(FieldValue.String).ToArray()))));
                    }

                    // Raw Lucene filter (optional)
                    if (!string.IsNullOrWhiteSpace(_cfg.AdditionalQueryFilter))
                    {
                        filterClauses.Add(f => f.QueryString(qs =>
                            qs.Query(_cfg.AdditionalQueryFilter)));
                    }

                    b.Filter(filterClauses.ToArray());

                    // ── MustNot clauses ───────────────────────────────────────
                    if (_cfg.ExcludeServices.Count > 0)
                    {
                        b.MustNot(mn => mn.Terms(t => t
                            .Field(_cfg.ServiceField)
                            .Terms(new TermsQueryField(
                                _cfg.ExcludeServices.Select(FieldValue.String).ToArray()))));
                    }
                }));

                return s;
            }, ct);

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("ES query returned invalid response: {Reason}",
                    response.ElasticsearchServerError?.Error?.Reason);
                return new List<LogEntry>();
            }

            return response.Hits
                .Where(h => h.Source.HasValue)
                .Select(h => MapToLogEntry(h.Id ?? string.Empty, h.Source!.Value))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception querying Elasticsearch");
            return new List<LogEntry>();
        }
    }

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
        // Supports dot-notation: "error.message", "kubernetes.pod.name"
        string GetString(string fieldPath)
        {
            var parts = fieldPath.Split('.');
            var current = doc;
            foreach (var part in parts)
            {
                if (!current.TryGetProperty(part, out var next)) return string.Empty;
                current = next;
            }
            return current.ValueKind == JsonValueKind.String
                ? current.GetString() ?? string.Empty
                : current.ToString();
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
            fields[prop.Name] = prop.Value.ToString();

        return new LogEntry
        {
            Id            = id,
            Timestamp     = GetTimestamp(),
            Level         = GetString(_cfg.LevelField),
            Message       = GetString(_cfg.MessageField),
            Exception     = GetString(_cfg.ExceptionField),
            Service       = GetString(_cfg.ServiceField),
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
