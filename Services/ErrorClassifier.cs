using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using LogWatcher.Configuration;
using LogWatcher.Models;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogWatcher.Services;

/// <summary>
/// Uses Claude to classify log errors:
///  - Severity + fixability + confidence score
///  - Root cause category, suggested area, reproduction hints
///  - Similar error grouping + spike escalation
///  - Tech-stack-aware prompts
/// </summary>
public class ErrorClassifier
{
    private readonly AnthropicClient _anthropic;
    private readonly ClassifierSettings _cfg;
    private readonly ILogger<ErrorClassifier> _logger;

    private static readonly string[] RootCauseCategories =
    [
        "NullReferenceException", "ArgumentException", "InvalidOperationException",
        "DatabaseTimeout", "DatabaseConnectionFailed", "UniqueConstraintViolation",
        "DeadlockDetected", "HttpClientTimeout", "HttpClientBadResponse",
        "AuthenticationFailure", "AuthorizationFailure", "JsonDeserializationError",
        "QueueConnectionFailed", "CacheConnectionFailed", "FileIOError",
        "ConfigurationMissing", "UnhandledEdgeCase", "ConcurrencyConflict",
        "MemoryPressure", "InfrastructureFailure", "Unknown"
    ];

    // Compiled regexes for message normalisation
    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex NumericIdRegex = new(
        @"([=#])\d+",
        RegexOptions.Compiled);

    private static readonly Regex IpRegex = new(
        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
        RegexOptions.Compiled);

    private static readonly Regex FenceRegex = new(
        @"```[\w]*\n?|```",
        RegexOptions.Compiled);

    public ErrorClassifier(IOptions<AppSettings> options, ILogger<ErrorClassifier> logger)
    {
        _cfg = options.Value.Classifier;
        _logger = logger;
        _anthropic = new AnthropicClient(options.Value.Anthropic.ApiKey);
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<List<ClassifiedError>> ClassifyAsync(
        List<LogEntry> logs, List<SpikeEvent> spikes, CancellationToken ct)
    {
        if (logs.Count == 0) return [];

        var spikeMap = spikes.ToDictionary(s => s.Fingerprint);

        if (_cfg.GroupSimilarErrors)
        {
            var groups = logs
                .GroupBy(ComputeFingerprint)
                .ToList();

            var representatives = groups
                .Select(g => g.OrderByDescending(l => l.Timestamp).First())
                .ToList();

            _logger.LogInformation(
                "Grouping: {Raw} logs → {Groups} unique groups to classify",
                logs.Count, groups.Count);

            var results = await RunBatchAsync(representatives, ct);

            return results.Select(r =>
            {
                var fp = ComputeFingerprint(r.OriginalLog);
                var grp = groups.FirstOrDefault(g => g.Key == fp);

                r.GroupedLogs = grp?.ToList() ?? [];
                r.ErrorFingerprint = fp;

                if (spikeMap.TryGetValue(fp, out var spike))
                {
                    r.IsSpike = true;
                    r.SpikeCount = spike.Count;
                    r.Severity = "critical";
                    r.Title = $"[SPIKE x{spike.Count}] {r.Title}";
                }

                return r;
            }).ToList();
        }
        else
        {
            var results = await RunBatchAsync(logs, ct);
            foreach (var r in results)
            {
                r.ErrorFingerprint = ComputeFingerprint(r.OriginalLog);
                if (spikeMap.TryGetValue(r.ErrorFingerprint, out var spike))
                {
                    r.IsSpike = true;
                    r.SpikeCount = spike.Count;
                    r.Severity = "critical";
                    r.Title = $"[SPIKE x{spike.Count}] {r.Title}";
                }
            }
            return results;
        }
    }

    // ── Claude API call ───────────────────────────────────────────────────────

    private async Task<List<ClassifiedError>> RunBatchAsync(
        List<LogEntry> logs, CancellationToken ct)
    {
        var batchItems = logs.Select((l, i) => new
        {
            index     = i,
            service   = l.Service,
            level     = l.Level,
            message   = l.Message,
            exception = Truncate(l.Exception, 1000),
            context   = l.ContextFields.Count > 0 ? l.ContextFields : null
        }).ToList();

        var stackContext = _cfg.TechStack.Count > 0
            ? $"Tech stack: {string.Join(", ", _cfg.TechStack)}."
            : string.Empty;

        var codebaseContext = !string.IsNullOrEmpty(_cfg.CodebaseContext)
            ? $"Codebase notes: {_cfg.CodebaseContext}"
            : string.Empty;

        var rootCauseList = string.Join(", ", RootCauseCategories);
        var batchJson = JsonSerializer.Serialize(batchItems,
            new JsonSerializerOptions { WriteIndented = false });

        var promptText =
            "You are a senior software engineer performing production incident triage.\n" +
            stackContext + "\n" +
            codebaseContext + "\n\n" +
            "Analyze these application log errors and classify each one carefully.\n\n" +
            "Log entries (JSON):\n" + batchJson + "\n\n" +
            "For EACH entry return a JSON object with these fields:\n" +
            "- index: (integer, same as input)\n" +
            "- severity: \"critical\" | \"error\" | \"warning\"\n" +
            "- is_fixable_by_code: boolean — true ONLY if a developer can fix this by changing application code.\n" +
            "  Set false for: infra failures (OOM, disk full, network unreachable), missing env vars, TLS issues.\n" +
            "- confidence: integer 0-100\n" +
            "- root_cause_category: one of [" + rootCauseList + "]\n" +
            "- title: concise GitHub issue title, max 80 chars, include service name prefix if available\n" +
            "- summary: 3-4 sentence markdown paragraph covering what failed, trigger, impact, context\n" +
            "- suggested_area: most likely file, class, method or layer; write \"unknown\" if unsure\n" +
            "- reproduction_hints: array of 1-3 short strings. Empty array if no hints.\n\n" +
            "Rules:\n" +
            "- Respond ONLY with a JSON array, no markdown code fences, no preamble.\n" +
            "- For noise (cancelled requests, client disconnects), set confidence < 40 and is_fixable_by_code false.\n";

        try
        {
            var request = new MessageParameters
            {
                Model = AnthropicModels.Claude3Haiku,
                MaxTokens = 4096,
                Messages = [new Message(RoleType.User, promptText)]
            };

            var response = await _anthropic.Messages.GetClaudeMessageAsync(request, ct);

            var rawText = response.Content
                .OfType<TextContent>()
                .FirstOrDefault()?.Text ?? "[]";

            // Strip accidental markdown fences
            var json = FenceRegex.Replace(rawText.Trim(), "").Trim();

            var results = JsonSerializer.Deserialize<List<ClaudeResult>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (results == null) return [];

            return results
                .Where(r => r.IsFixableByCode && r.Confidence >= _cfg.MinConfidenceScore
                            && r.Index >= 0 && r.Index < logs.Count)
                .Select(r =>
                {
                    var log = logs[r.Index];
                    var labels = _cfg.SeverityLabels.TryGetValue(r.Severity, out var l)
                        ? l
                        : ["bug", "auto-detected"];

                    return new ClassifiedError
                    {
                        OriginalLog       = log,
                        Severity          = r.Severity,
                        IsFixableByCode   = r.IsFixableByCode,
                        ConfidenceScore   = r.Confidence,
                        Title             = r.Title,
                        Summary           = r.Summary,
                        RootCauseCategory = r.RootCauseCategory,
                        SuggestedArea     = r.SuggestedArea,
                        ReproductionHints = r.ReproductionHints ?? [],
                        SuggestedLabels   = labels,
                        ErrorFingerprint  = ComputeFingerprint(log)
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Claude classification API");
            return [];
        }
    }

    // ── Fingerprinting ────────────────────────────────────────────────────────

    public static string ComputeFingerprint(LogEntry log)
    {
        var exceptionType = log.Exception?
            .Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        var normalisedMsg = NormaliseMessage(log.Message);
        var msgPrefix = Truncate(normalisedMsg, 120) ?? string.Empty;

        var raw = $"{log.Service}|{exceptionType}|{msgPrefix}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private static string NormaliseMessage(string msg)
    {
        msg = GuidRegex.Replace(msg, "{guid}");
        msg = NumericIdRegex.Replace(msg, "$1{n}");
        msg = IpRegex.Replace(msg, "{ip}");
        return msg;
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length > max ? s[..max] + "..." : s;

    // ── Response DTO ──────────────────────────────────────────────────────────

    private sealed class ClaudeResult
    {
        public int Index { get; set; }
        public string Severity { get; set; } = "error";
        [JsonPropertyName("is_fixable_by_code")]
        public bool IsFixableByCode { get; set; }
        public int Confidence { get; set; }
        public string RootCauseCategory { get; set; } = "Unknown";
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string SuggestedArea { get; set; } = "unknown";
        public List<string>? ReproductionHints { get; set; }
    }
}
