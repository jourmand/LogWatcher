# LogWatcher

LogWatcher now supports two related automation flows:

1. **Autonomous bug-fixing pipeline**: reads Elasticsearch error logs, classifies them with Claude, creates GitHub Issues, and tracks Copilot-generated pull requests.
2. **Freshservice triage pipeline**: polls new Freshservice tickets, matches them against similar closed tickets, extracts likely fix hints from historical conversations, and reassigns the new ticket to the most relevant team.

Elasticsearch can be queried either through the direct .NET Elasticsearch client or through the Elastic MCP server.

## Architecture

### Log-driven GitHub automation

```
Elasticsearch (direct client or MCP) → Log Poller → AI Classifier → Deduplicator → GitHub Issue → Copilot Agent → PR
                                                                                                         ↓
                                                                                             Slack notification
```

Workers for this flow:
- **LogWatcherWorker** — polls Elasticsearch, detects spikes, classifies errors, creates GitHub issues
- **PrPollerWorker** — polls GitHub for Copilot PR status and notifies when PRs are ready for review

### Freshservice triage

```
Freshservice tickets → Similarity matching against closed tickets → Fix hint extraction → Team reassignment
```

Worker for this flow:
- **FreshServiceWorker** — polls Freshservice, matches new tickets to historical closed tickets, and logs assignment/fix recommendations

## Current runtime default

The current `Program.cs` registers **FreshServiceWorker** by default.

`LogWatcherWorker` and `PrPollerWorker` are still present in the codebase but are commented out in dependency registration. If you want the GitHub automation pipeline to run, re-enable those hosted services in `Program.cs`.

## Prerequisites

1. **.NET 8 SDK**
2. **Elasticsearch** with application logs if you want the log-driven pipeline
3. **Node.js** if you want to use the Elastic MCP server with `Transport = npx`
4. **Docker** if you want to use the Elastic MCP server with `Transport = docker`
5. **GitHub Copilot Pro/Pro+/Business/Enterprise** subscription for the GitHub automation flow
6. **Copilot cloud agent enabled** on your repository
   (Repo Settings → Copilot → Coding agent → Enable)
7. **GitHub Personal Access Token** (fine-grained) with:
   - Actions: Read & Write
   - Contents: Read & Write
   - Issues: Read & Write
   - Pull Requests: Read & Write
8. **Anthropic API key** for error classification in the log-driven pipeline
9. **Freshservice API key** and base URL if you want the Freshservice triage flow
10. **(Optional)** Slack incoming webhook URL for GitHub issue / PR notifications

## Quick Start

```bash
# 1. Clone / copy the project
cd LogWatcher

# 2. Fill in your credentials
#    Edit appsettings.json or use environment variables

# 3. Run the currently registered worker(s)
dotnet run
```

By default, this starts the Freshservice triage worker. To run the Elasticsearch → GitHub automation flow, uncomment `LogWatcherWorker` and `PrPollerWorker` in `Program.cs`.

## Configuration

Edit `appsettings.json` or set environment variables:

### Elasticsearch

| Setting | Env var | Description |
|---------|---------|-------------|
| `Elasticsearch:Url` | `AppSettings__Elasticsearch__Url` | Elasticsearch endpoint |
| `Elasticsearch:Username` | `AppSettings__Elasticsearch__Username` | Basic auth username |
| `Elasticsearch:Password` | `AppSettings__Elasticsearch__Password` | Basic auth password |
| `Elasticsearch:ApiKey` | `AppSettings__Elasticsearch__ApiKey` | API key for direct client mode |
| `Elasticsearch:IndexPattern` | `AppSettings__Elasticsearch__IndexPattern` | Index pattern, for example `demoapp-logs-*` |
| `Elasticsearch:LevelField` | `AppSettings__Elasticsearch__LevelField` | Field name for log level |
| `Elasticsearch:MessageField` | `AppSettings__Elasticsearch__MessageField` | Field name for log message |
| `Elasticsearch:ExceptionField` | `AppSettings__Elasticsearch__ExceptionField` | Field name for exception payload |
| `Elasticsearch:ServiceField` | `AppSettings__Elasticsearch__ServiceField` | Field name for service name |
| `Elasticsearch:TimestampField` | `AppSettings__Elasticsearch__TimestampField` | Field name for timestamp |
| `Elasticsearch:MaxResultsPerPoll` | `AppSettings__Elasticsearch__MaxResultsPerPoll` | Max logs fetched per polling cycle |
| `Elasticsearch:AdditionalQueryFilter` | `AppSettings__Elasticsearch__AdditionalQueryFilter` | Additional Lucene filter for direct mode |

### Elasticsearch MCP

| Setting | Env var | Description |
|---------|---------|-------------|
| `Watcher:ElasticsearchSource` | `AppSettings__Watcher__ElasticsearchSource` | `mcp`, `direct`, or `pulling` |
| `Mcp:UseElasticsearchMcp` | `AppSettings__Mcp__UseElasticsearchMcp` | Fallback switch when source is not explicitly set |
| `Mcp:Transport` | `AppSettings__Mcp__Transport` | `npx` or `docker` |
| `Mcp:EsApiKey` | `AppSettings__Mcp__EsApiKey` | API key passed to the MCP server process |
| `Mcp:TimeoutSeconds` | `AppSettings__Mcp__TimeoutSeconds` | Timeout for a single MCP search call |

When `Watcher:ElasticsearchSource` is set to `mcp`, LogWatcher launches the official Elastic MCP server as a child process and uses its `search` tool to query Elasticsearch.

### GitHub and classification

| Setting | Env var | Description |
|---------|---------|-------------|
| `GitHub:PersonalAccessToken` | `AppSettings__GitHub__PersonalAccessToken` | GitHub PAT |
| `GitHub:Owner` | `AppSettings__GitHub__Owner` | GitHub username or organization |
| `GitHub:Repo` | `AppSettings__GitHub__Repo` | Repository name |
| `GitHub:MaxIssuesPerHour` | `AppSettings__GitHub__MaxIssuesPerHour` | Issue creation rate limit guard |
| `Anthropic:ApiKey` | `AppSettings__Anthropic__ApiKey` | Claude API key |
| `Watcher:PollIntervalSeconds` | `AppSettings__Watcher__PollIntervalSeconds` | Log poll interval |
| `Watcher:PrPollIntervalSeconds` | `AppSettings__Watcher__PrPollIntervalSeconds` | PR polling interval |
| `Watcher:DeduplicationWindowHours` | `AppSettings__Watcher__DeduplicationWindowHours` | Deduplication window |
| `Classifier:MinConfidenceScore` | `AppSettings__Classifier__MinConfidenceScore` | Minimum classifier confidence |
| `Notifications:SlackWebhookUrl` | `AppSettings__Notifications__SlackWebhookUrl` | Slack webhook for notifications |

### Freshservice

| Setting | Env var | Description |
|---------|---------|-------------|
| `FreshService:Enabled` | `AppSettings__FreshService__Enabled` | Enables the Freshservice worker |
| `FreshService:BaseUrl` | `AppSettings__FreshService__BaseUrl` | Freshservice base URL |
| `FreshService:ApiKey` | `AppSettings__FreshService__ApiKey` | Freshservice API key |
| `FreshService:PollIntervalSeconds` | `AppSettings__FreshService__PollIntervalSeconds` | Ticket polling interval |
| `FreshService:RecentClosedLookbackDays` | `AppSettings__FreshService__RecentClosedLookbackDays` | Closed-ticket history window |
| `FreshService:InitialNewTicketLookbackMinutes` | `AppSettings__FreshService__InitialNewTicketLookbackMinutes` | Initial scan window for new tickets |
| `FreshService:MaxPages` | `AppSettings__FreshService__MaxPages` | Maximum ticket pages fetched per cycle |
| `FreshService:PerPage` | `AppSettings__FreshService__PerPage` | Tickets per API request |
| `FreshService:SimilarityThreshold` | `AppSettings__FreshService__SimilarityThreshold` | Minimum similarity score before reassignment |

The Freshservice worker:

- fetches recently updated tickets
- finds similar closed tickets using token-based similarity
- extracts likely fix notes from older ticket conversations
- reassigns the new ticket to the matched group, department, or responder when possible
- stores checkpoints and analysis results in SQLite

## Docker

```bash
docker build -t logwatcher .

docker run -d \
  --name logwatcher \
  -v logwatcher-data:/app/data \
  -e AppSettings__Elasticsearch__Url=http://your-es:9200 \
   -e AppSettings__Watcher__ElasticsearchSource=direct \
  -e AppSettings__GitHub__PersonalAccessToken=ghp_xxx \
  -e AppSettings__GitHub__Owner=your-username \
  -e AppSettings__GitHub__Repo=your-repo \
  -e AppSettings__Anthropic__ApiKey=sk-ant-xxx \
   -e AppSettings__FreshService__Enabled=true \
   -e AppSettings__FreshService__BaseUrl=https://your-company.freshservice.com \
   -e AppSettings__FreshService__ApiKey=fs_xxx \
  -e AppSettings__Notifications__SlackWebhookUrl=https://hooks.slack.com/... \
  logwatcher
```

The current container image does not include Node.js or the Docker CLI, so `Mcp:Transport = npx` and `Mcp:Transport = docker` are not available inside the stock image without extending it first.

## Elasticsearch field mapping

The log watcher maps these fields from your log documents:

| Config key | Default | Typical Serilog/NLog value |
|-----------|---------|---------------------------|
| `LevelField` | `level` | `level` / `Level` |
| `MessageField` | `message` | `message` / `@message` |
| `ExceptionField` | `exception` | `exception` |
| `ServiceField` | `service` | `ServiceName` / `Application` |
| `TimestampField` | `@timestamp` | `@timestamp` |

## Important notes

- **Always review Copilot PRs** before merging; this project is designed to assist remediation, not to auto-merge fixes into production
- The SQLite database (`logwatcher.db`) stores both log/fingerprint state and Freshservice analysis checkpoints
- Rate limiting is enforced for GitHub issue creation: 5 issues/hour by default
- Only code-fixable errors are escalated into GitHub issues; infrastructure failures are skipped
- Freshservice matching is heuristic, so assignment changes and extracted fix hints should still be reviewed by a human
