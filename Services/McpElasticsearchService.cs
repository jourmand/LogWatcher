using Dapper;
using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogWatcher.Services;

/// <summary>
/// Fetches error logs from Elasticsearch via the official Elastic MCP server
/// (https://github.com/elastic/mcp-server-elasticsearch).
///
/// The MCP server process is launched as a child process communicating over
/// stdio (npx @elastic/mcp-server-elasticsearch OR docker).
/// Claude is asked to call the "search" tool with a structured ES query,
/// and the results are parsed back into LogEntry objects.
///
/// Falls back gracefully: if the MCP server cannot be started, the caller
/// should use ElasticsearchPoller instead (controlled by McpSettings.UseElasticsearchMcp).
/// </summary>
public sealed class McpElasticsearchService : IAsyncDisposable
{
    private readonly McpSettings _mcpCfg;
    private readonly ElasticsearchSettings _esCfg;
    private readonly ILogger<McpElasticsearchService> _logger;
    private readonly string _connectionString;
    private readonly List<Regex> _excludeRegexes;

    // Lazily-initialised MCP client — created on first poll
    private McpClient? _mcpClient;
    private bool _initialised;

    public McpElasticsearchService(
        IOptions<AppSettings> options,
        ILogger<McpElasticsearchService> logger)
    {
        _mcpCfg = options.Value.Mcp;
        _esCfg  = options.Value.Elasticsearch;
        _logger = logger;

        var dbPath = Path.Combine(AppContext.BaseDirectory, "logwatcher.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureCheckpointTable();

        _excludeRegexes = _esCfg.ExcludeMessageRegex
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches new error log entries since the last checkpoint using the ES MCP server.
    /// </summary>
    public async Task<List<LogEntry>> FetchNewErrorsAsync(CancellationToken ct)
    {
        await EnsureInitialisedAsync(ct);

        var from = LoadCheckpoint();
        var to   = DateTime.UtcNow;

        _logger.LogDebug("[MCP] Poll window: {From:O} → {To:O}", from, to);

        try
        {
            var results = await SearchViaToolAsync(from, to, ct);
            if (results.Count > 0) SaveCheckpoint(to);

            var filtered = ApplyClientSideFilters(results);
            _logger.LogInformation(
                "[MCP] Retrieved {Raw} docs, {Filtered} after exclusion filters",
                results.Count, filtered.Count);

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MCP] Error fetching logs via MCP");
            return [];
        }
    }

    public List<SpikeEvent> DetectSpikes(List<LogEntry> batch)
    {
        if (!_esCfg.EnableSpikeDetection || batch.Count == 0)
            return [];

        var spikes = batch
            .GroupBy(l => ErrorClassifier.ComputeFingerprint(l))
            .Where(g => g.Count() >= _esCfg.SpikeThreshold)
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
            _logger.LogWarning("[MCP] {Count} spike(s) detected", spikes.Count);

        return spikes;
    }

    // ── MCP initialisation ────────────────────────────────────────────────────

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_initialised) return;

        _logger.LogInformation("[MCP] Launching Elasticsearch MCP server ({Transport})…",
            _mcpCfg.Transport);

        var env = new Dictionary<string, string?>
        {
            ["ES_URL"]            = _esCfg.Url,
            ["ES_API_KEY"]        = _mcpCfg.EsApiKey,
            ["OTEL_LOG_LEVEL"]    = "none"   // suppress telemetry noise
        };

        // Also support basic-auth setups (no API key)
        if (string.IsNullOrEmpty(_mcpCfg.EsApiKey) && !string.IsNullOrEmpty(_esCfg.Username))
        {
            env["ES_USERNAME"] = _esCfg.Username;
            env["ES_PASSWORD"] = _esCfg.Password;
        }

        StdioClientTransport transport;

        if (_mcpCfg.Transport == "docker")
        {
            // docker run -i --rm -e ES_URL -e ES_API_KEY docker.elastic.co/mcp/elasticsearch stdio
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command   = "docker",
                Arguments =
                [
                    "run", "-i", "--rm",
                    "-e", "ES_URL",
                    "-e", "ES_API_KEY",
                    "-e", "OTEL_LOG_LEVEL",
                    "docker.elastic.co/mcp/elasticsearch",
                    "stdio"
                ],
                EnvironmentVariables = env,
                Name = "elasticsearch-mcp"
            });
        }
        else
        {
            // npx -y @elastic/mcp-server-elasticsearch  (default)
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command   = "npx",
                Arguments = ["-y", "@elastic/mcp-server-elasticsearch"],
                EnvironmentVariables = env,
                Name = "elasticsearch-mcp"
            });
        }

        _mcpClient = await McpClient.CreateAsync(transport,
            cancellationToken: ct);

        var tools = await _mcpClient.ListToolsAsync(cancellationToken: ct);
        _logger.LogInformation(
            "[MCP] Connected. Available tools: {Tools}",
            string.Join(", ", tools.Select(t => t.Name)));

        _initialised = true;
    }

    // ── ES search via MCP tool call ───────────────────────────────────────────

    private async Task<List<LogEntry>> SearchViaToolAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        if (_mcpClient == null)
            throw new InvalidOperationException("MCP client not initialised.");

        // Build the Elasticsearch Query DSL we want the MCP server to execute
        var query = BuildEsQuery(from, to);
        var queryJson = JsonSerializer.Serialize(query);

        _logger.LogDebug("[MCP] Calling search tool with query: {Query}", queryJson);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_mcpCfg.TimeoutSeconds));

        // Call the "search" tool exposed by @elastic/mcp-server-elasticsearch
        var result = await _mcpClient.CallToolAsync(
            "search",
            new Dictionary<string, object?>
            {
                ["index"] = _esCfg.IndexPattern,
                ["body"]  = query
            },
            cancellationToken: timeout.Token);

        return ParseSearchResult(result);
    }

    private object BuildEsQuery(DateTime from, DateTime to)
    {
        var mustNot = new List<object>();
        if (_esCfg.ExcludeServices.Count > 0)
        {
            mustNot.Add(new
            {
                terms = new Dictionary<string, object>
                {
                    [_esCfg.ServiceField] = _esCfg.ExcludeServices
                }
            });
        }

        var filters = new List<object>
        {
            new
            {
                range = new Dictionary<string, object>
                {
                    [_esCfg.TimestampField] = new
                    {
                        gte = from.ToString("O"),
                        lt  = to.ToString("O")
                    }
                }
            },
            new
            {
                terms = new Dictionary<string, object>
                {
                    [_esCfg.LevelField] = _esCfg.ErrorLevels
                }
            }
        };

        if (_esCfg.IncludeServices.Count > 0)
        {
            filters.Add(new
            {
                terms = new Dictionary<string, object>
                {
                    [_esCfg.ServiceField] = _esCfg.IncludeServices
                }
            });
        }

        return new
        {
            size = _esCfg.MaxResultsPerPoll,
            sort = new[] { new Dictionary<string, object> { [_esCfg.TimestampField] = new { order = "asc" } } },
            _source = BuildSourceFields(),
            query = new
            {
                @bool = new
                {
                    filter   = filters,
                    must_not = mustNot.Count > 0 ? mustNot : null
                }
            }
        };
    }

    private List<string> BuildSourceFields()
    {
        var fields = new List<string>
        {
            _esCfg.LevelField, _esCfg.MessageField, _esCfg.ExceptionField,
            _esCfg.ServiceField, _esCfg.TimestampField,
            "exceptions", "messageTemplate", "fields"
        };
        fields.AddRange(_esCfg.ExtraContextFields);
        return fields.Distinct().ToList();
    }

    // ── Parse MCP tool result → LogEntry list ─────────────────────────────────

    private List<LogEntry> ParseSearchResult(CallToolResult result)
    {
        var entries = new List<LogEntry>();

        // MCP tool result comes back as a list of Content items
        // The search tool returns JSON text
        foreach (var content in result.Content)
        {
            if (content is not TextContentBlock textContent || string.IsNullOrWhiteSpace(textContent.Text))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(textContent.Text);
                var root = doc.RootElement;

                // ES response shape: { hits: { hits: [ { _id, _source } ] } }
                if (!root.TryGetProperty("hits", out var hitsOuter)) continue;
                if (!hitsOuter.TryGetProperty("hits", out var hitsArray)) continue;

                foreach (var hit in hitsArray.EnumerateArray())
                {
                    var id = hit.TryGetProperty("_id", out var idEl)
                        ? idEl.GetString() ?? string.Empty
                        : string.Empty;

                    if (!hit.TryGetProperty("_source", out var source)) continue;

                    entries.Add(MapToLogEntry(id, source));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[MCP] Failed to parse search result JSON");
            }
        }

        return entries;
    }

    private LogEntry MapToLogEntry(string id, JsonElement doc)
    {
        string GetString(string fieldPath)
        {
            var parts   = fieldPath.Split('.');
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
            var raw = GetString(_esCfg.TimestampField);
            return DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
        }

        var contextFields = new Dictionary<string, string>();
        foreach (var field in _esCfg.ExtraContextFields)
        {
            var value = GetString(field);
            if (!string.IsNullOrEmpty(value)) contextFields[field] = value;
        }

        var fields = new Dictionary<string, object?>();
        foreach (var prop in doc.EnumerateObject())
            fields[prop.Name] = prop.Value.Clone();

        var exception = FirstNonEmpty(
            GetString(_esCfg.ExceptionField),
            GetString("exceptions.0.StackTraceString"),
            GetString("exceptions.0.Message"),
            BuildExceptionFallback());

        var service = FirstNonEmpty(
            GetString(_esCfg.ServiceField),
            GetString("service"),
            GetString("fields.service"),
            GetString("fields.Service"));

        return new LogEntry
        {
            Id            = id,
            Timestamp     = GetTimestamp(),
            Level         = GetString(_esCfg.LevelField),
            Message       = GetString(_esCfg.MessageField),
            Exception     = exception,
            Service       = service,
            ContextFields = contextFields,
            Fields        = fields
        };
    }

    // ── Client-side filters ───────────────────────────────────────────────────

    private List<LogEntry> ApplyClientSideFilters(List<LogEntry> entries) =>
        entries.Where(e => !IsExcluded(e)).ToList();

    private bool IsExcluded(LogEntry entry)
    {
        var msg = entry.Message;
        foreach (var pattern in _esCfg.ExcludeMessagePatterns)
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var regex in _excludeRegexes)
            if (regex.IsMatch(msg)) return true;
        return false;
    }

    // ── Checkpoint ────────────────────────────────────────────────────────────

    private void EnsureCheckpointTable()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "CREATE TABLE IF NOT EXISTS poll_checkpoint (" +
            "  id INTEGER PRIMARY KEY CHECK (id = 1)," +
            "  last_polled TEXT NOT NULL" +
            ");");
    }

    private DateTime LoadCheckpoint()
    {
        using var conn = new SqliteConnection(_connectionString);
        var stored = conn.QueryFirstOrDefault<string>(
            "SELECT last_polled FROM poll_checkpoint WHERE id = 1");
        if (stored != null && DateTime.TryParse(stored, out var dt))
            return dt;
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

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is IAsyncDisposable d)
            await d.DisposeAsync();
    }
}
