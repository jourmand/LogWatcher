using Dapper;
using LogWatcher.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogWatcher.Services;

public sealed class FreshServiceService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "with", "from", "this", "that", "your", "have", "has", "had",
        "not", "are", "was", "were", "can", "could", "would", "should", "into", "http", "https",
        "api", "app", "error", "issue", "ticket", "user", "users", "please", "help"
    ];

    private readonly FreshServiceSettings _cfg;
    private readonly HttpClient _http;
    private readonly ILogger<FreshServiceService> _logger;
    private readonly string _connectionString;

    public FreshServiceService(
        IOptions<AppSettings> options,
        IHttpClientFactory httpFactory,
        ILogger<FreshServiceService> logger)
    {
        _cfg = options.Value.FreshService;
        _http = httpFactory.CreateClient("freshservice");
        _logger = logger;

        var dbPath = Path.Combine(AppContext.BaseDirectory, "logwatcher.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    public bool IsEnabled => _cfg.Enabled;

    public async Task<List<FreshServiceTriageResult>> AnalyzeNewTicketsAsync(CancellationToken ct)
    {
        var from = DateTime.UtcNow.AddDays(-10);//LoadCheckpoint() ?? DateTime.UtcNow.AddMinutes(-_cfg.InitialNewTicketLookbackMinutes);
        var to = DateTime.UtcNow;

        var openCandidates = await FetchTicketsUpdatedSinceAsync(from, ct);
        var newTickets = openCandidates
            .Where(t => !IsClosedStatus(t.Status))// && !IsAlreadyAnalyzed(t.Id))
            .ToList();

        if (newTickets.Count == 0)
        {
            SaveCheckpoint(to);
            return [];
        }

        var closedSince = DateTime.UtcNow.AddDays(-_cfg.RecentClosedLookbackDays);
        var closedTickets = (await FetchTicketsUpdatedSinceAsync(closedSince, ct))
            .Where(t => IsClosedStatus(t.Status))
            .ToList();

        var results = new List<FreshServiceTriageResult>();

        foreach (var ticket in newTickets)
        {
            var best = FindBestMatch(ticket, closedTickets);
            if (best == null || best.Value.Score < _cfg.SimilarityThreshold)
            {
                PersistAnalysis(ticket.Id, null, null, null);
                continue;
            }

            var fix = await TryExtractFixAsync(best.Value.Ticket.Id, ct);
            var team = GetResponsibleTeam(best.Value.Ticket);
            var assignmentUpdated = await AssignTicketToMatchedTeamAsync(ticket, best.Value.Ticket, ct);

            var triage = new FreshServiceTriageResult
            {
                NewTicketId = ticket.Id,
                NewTicketSubject = ticket.Subject ?? string.Empty,
                MatchedClosedTicketId = best.Value.Ticket.Id,
                SimilarityScore = best.Value.Score,
                ResponsibleTeam = team,
                AssignmentUpdated = assignmentUpdated,
                ExistingFix = fix,
                MatchedClosedTicketUrl = BuildTicketUrl(best.Value.Ticket.Id)
            };

            PersistAnalysis(ticket.Id, best.Value.Ticket.Id, team, fix);
            results.Add(triage);
        }

        SaveCheckpoint(to);
        return results;
    }

    private async Task<List<FreshTicket>> FetchTicketsUpdatedSinceAsync(DateTime updatedSince, CancellationToken ct)
    {
        var all = new List<FreshTicket>();

        for (var page = 1; page <= _cfg.MaxPages; page++)
        {
            var url = $"api/v2/tickets?updated_since={Uri.EscapeDataString(updatedSince.ToString("O"))}&page={page}&per_page={_cfg.PerPage}";
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("FreshService tickets call failed {Status}: {Body}", (int)response.StatusCode, body);
                break;
            }

            var bodyJson = await response.Content.ReadAsStringAsync(ct);
            List<FreshTicket> pageTickets;

            try
            {
                pageTickets = ParseTickets(bodyJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "FreshService tickets JSON parse failed. Endpoint: {Url}. Payload preview: {Preview}",
                    url,
                    Truncate(bodyJson, 350));
                break;
            }

            if (pageTickets.Count == 0) break;

            all.AddRange(pageTickets);
            if (pageTickets.Count < _cfg.PerPage) break;
        }

        return all;
    }

    private async Task<string?> TryExtractFixAsync(long ticketId, CancellationToken ct)
    {
        var url = $"api/v2/tickets/{ticketId}/conversations?page=1&per_page=30";
        using var response = await _http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
            return null;

        var bodyJson = await response.Content.ReadAsStringAsync(ct);
        List<FreshConversation> conversations;

        try
        {
            conversations = ParseConversations(bodyJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "FreshService conversations JSON parse failed for ticket #{TicketId}. Payload preview: {Preview}",
                ticketId,
                Truncate(bodyJson, 350));
            return null;
        }

        // Pick first note/comment that likely contains a resolution/fix hint.
        foreach (var conv in conversations.OrderByDescending(c => c.CreatedAt))
        {
            var text = HtmlToText(conv.BodyText ?? conv.BodyHtml ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!LooksLikeFix(text)) continue;

            return Truncate(FirstSentence(text), 320);
        }

        return null;
    }

    private static bool LooksLikeFix(string text)
    {
        var lowered = text.ToLowerInvariant();
        return lowered.Contains("fix")
               || lowered.Contains("resolved")
               || lowered.Contains("workaround")
               || lowered.Contains("solution")
               || lowered.Contains("root cause")
               || lowered.Contains("patch")
               || lowered.Contains("upgrade")
               || lowered.Contains("restart");
    }

    private (FreshTicket Ticket, double Score)? FindBestMatch(FreshTicket source, List<FreshTicket> closed)
    {
        var sourceText = $"{source.Subject} {source.DescriptionText}";
        var sourceTokens = Tokenize(sourceText);

        FreshTicket? bestTicket = null;
        var bestScore = 0.0;

        foreach (var candidate in closed)
        {
            var candidateText = $"{candidate.Subject} {candidate.DescriptionText}";
            var score = Similarity(sourceTokens, Tokenize(candidateText));

            if (score <= bestScore) continue;
            bestScore = score;
            bestTicket = candidate;
        }

        return bestTicket == null ? null : (bestTicket, bestScore);
    }

    private static string GetResponsibleTeam(FreshTicket ticket)
    {
        if (ticket.GroupId.HasValue) return $"group:{ticket.GroupId.Value}";
        if (ticket.ResponderId.HasValue) return $"agent:{ticket.ResponderId.Value}";
        if (ticket.DepartmentId.HasValue) return $"department:{ticket.DepartmentId.Value}";
        return "unknown";
    }

    private static HashSet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static double Similarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;

        var intersection = left.Intersect(right).Count();
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static bool IsClosedStatus(int? status) => status is 4 or 5;

    private async Task<bool> AssignTicketToMatchedTeamAsync(
        FreshTicket newTicket,
        FreshTicket matchedClosedTicket,
        CancellationToken ct)
    {
        object? payload = null;
        string target = "unknown";

        if (matchedClosedTicket.GroupId.HasValue)
        {
            if (newTicket.GroupId == matchedClosedTicket.GroupId) return false;
            payload = new { group_id = matchedClosedTicket.GroupId.Value };
            target = $"group:{matchedClosedTicket.GroupId.Value}";
        }
        else if (matchedClosedTicket.DepartmentId.HasValue)
        {
            if (newTicket.DepartmentId == matchedClosedTicket.DepartmentId) return false;
            payload = new { department_id = matchedClosedTicket.DepartmentId.Value };
            target = $"department:{matchedClosedTicket.DepartmentId.Value}";
        }
        else if (matchedClosedTicket.ResponderId.HasValue)
        {
            if (newTicket.ResponderId == matchedClosedTicket.ResponderId) return false;
            payload = new { responder_id = matchedClosedTicket.ResponderId.Value };
            target = $"agent:{matchedClosedTicket.ResponderId.Value}";
        }

        if (payload == null)
        {
            _logger.LogDebug(
                "FreshService ticket #{TicketId}: no assignment fields found on matched closed ticket #{MatchedId}",
                newTicket.Id,
                matchedClosedTicket.Id);
            return false;
        }

        var url = $"api/v2/tickets/{newTicket.Id}";
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "FreshService assignment update failed for ticket #{TicketId} -> {Target}. Status={Status}. Body={Body}",
                newTicket.Id,
                target,
                (int)response.StatusCode,
                Truncate(body, 400));
            return false;
        }

        _logger.LogInformation(
            "FreshService ticket #{TicketId} assigned to {Target} (matched from closed ticket #{MatchedId})",
            newTicket.Id,
            target,
            matchedClosedTicket.Id);

        return true;
    }

    private bool IsAlreadyAnalyzed(long ticketId)
    {
        using var conn = new SqliteConnection(_connectionString);
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM freshservice_ticket_analysis WHERE ticket_id = @id",
            new { id = ticketId });
        return count > 0;
    }

    private DateTime? LoadCheckpoint()
    {
        using var conn = new SqliteConnection(_connectionString);
        var value = conn.QueryFirstOrDefault<string>(
            "SELECT last_polled FROM freshservice_poll_checkpoint WHERE id = 1");

        return value != null && DateTime.TryParse(value, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    private void SaveCheckpoint(DateTime utcNow)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "INSERT INTO freshservice_poll_checkpoint (id, last_polled) VALUES (1, @v) " +
            "ON CONFLICT(id) DO UPDATE SET last_polled = @v",
            new { v = utcNow.ToString("O") });
    }

    private void PersistAnalysis(long ticketId, long? matchedTicketId, string? team, string? fix)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "INSERT INTO freshservice_ticket_analysis (ticket_id, matched_closed_ticket_id, responsible_team, fix_excerpt, analyzed_at) " +
            "VALUES (@ticketId, @matched, @team, @fix, @now) " +
            "ON CONFLICT(ticket_id) DO UPDATE SET " +
            "matched_closed_ticket_id = excluded.matched_closed_ticket_id, " +
            "responsible_team = excluded.responsible_team, " +
            "fix_excerpt = excluded.fix_excerpt, " +
            "analyzed_at = excluded.analyzed_at",
            new
            {
                ticketId,
                matched = matchedTicketId,
                team,
                fix,
                now = DateTime.UtcNow.ToString("O")
            });
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(
            "CREATE TABLE IF NOT EXISTS freshservice_poll_checkpoint (" +
            "  id          INTEGER PRIMARY KEY CHECK (id = 1)," +
            "  last_polled TEXT NOT NULL" +
            ");" +
            "CREATE TABLE IF NOT EXISTS freshservice_ticket_analysis (" +
            "  ticket_id                INTEGER PRIMARY KEY," +
            "  matched_closed_ticket_id INTEGER," +
            "  responsible_team         TEXT," +
            "  fix_excerpt              TEXT," +
            "  analyzed_at              TEXT NOT NULL" +
            ");");
    }

    private string BuildTicketUrl(long ticketId)
    {
        if (string.IsNullOrWhiteSpace(_cfg.BaseUrl)) return ticketId.ToString();
        return $"{_cfg.BaseUrl.TrimEnd('/')}/a/tickets/{ticketId}";
    }

    private static string FirstSentence(string text)
    {
        var idx = text.IndexOfAny(['.', '!', '?']);
        return idx > 0 ? text[..(idx + 1)] : text;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var noTags = Regex.Replace(html, "<.*?>", " ");
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }

    private static List<FreshTicket> ParseTickets(string bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return [];

        using var doc = JsonDocument.Parse(bodyJson);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<FreshTicket>>(bodyJson, JsonOpts) ?? [];

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("tickets", out var ticketsElement)
                && ticketsElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<FreshTicket>>(ticketsElement.GetRawText(), JsonOpts) ?? [];
            }

            if (root.TryGetProperty("results", out var resultsElement)
                && resultsElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<FreshTicket>>(resultsElement.GetRawText(), JsonOpts) ?? [];
            }
        }

        throw new JsonException("Unsupported tickets JSON shape.");
    }

    private static List<FreshConversation> ParseConversations(string bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return [];

        using var doc = JsonDocument.Parse(bodyJson);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<FreshConversation>>(bodyJson, JsonOpts) ?? [];

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("conversations", out var convElement)
            && convElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<FreshConversation>>(convElement.GetRawText(), JsonOpts) ?? [];
        }

        throw new JsonException("Unsupported conversations JSON shape.");
    }

    private sealed class TicketsResponse
    {
        public List<FreshTicket> Tickets { get; set; } = [];
    }

    private sealed class ConversationsResponse
    {
        public List<FreshConversation> Conversations { get; set; } = [];
    }

    private sealed class FreshConversation
    {
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("body_text")]
        public string? BodyText { get; set; }

        [JsonPropertyName("body")]
        public string? BodyHtml { get; set; }
    }

    private sealed class FreshTicket
    {
        public long Id { get; set; }
        public string? Subject { get; set; }

        [JsonPropertyName("description_text")]
        public string? DescriptionText { get; set; }

        public int? Status { get; set; }

        [JsonPropertyName("group_id")]
        public long? GroupId { get; set; }

        [JsonPropertyName("responder_id")]
        public long? ResponderId { get; set; }

        [JsonPropertyName("department_id")]
        public long? DepartmentId { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}

public sealed class FreshServiceTriageResult
{
    public long NewTicketId { get; set; }
    public string NewTicketSubject { get; set; } = string.Empty;
    public long MatchedClosedTicketId { get; set; }
    public double SimilarityScore { get; set; }
    public string ResponsibleTeam { get; set; } = "unknown";
    public bool AssignmentUpdated { get; set; }
    public string? ExistingFix { get; set; }
    public string MatchedClosedTicketUrl { get; set; } = string.Empty;
}
