using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSessionsViewer.Models;

namespace CodexSessionsViewer.Services;

public sealed partial class ViewerService
{
    private const int SearchTextLimit = 50_000;
    // Keep enough index/event cache entries for large session sets to avoid frequent rebuild churn.
    private const int MaxCacheEntries = 2000;
    private static readonly TimeSpan SessionFilesCacheTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CostSummaryCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly string DefaultSessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");
    private static readonly string WindowsUsersDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users")
        : "/mnt/c/Users";
    private static readonly string[] WslNetworkRoots =
    [
        @"\\wsl.localhost",
        @"\\wsl$",
    ];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly string[] ContextMarkers =
    [
        "# agents.md instructions",
        "<environment_context>",
        "<collaboration_mode>",
        "<permissions instructions>",
    ];

    private readonly LabelStore _labelStore;
    private readonly ViewerSettingsStore _viewerSettings;
    private readonly ModelCatalogService _modelCatalog;
    private readonly ExchangeRateService _exchangeRates;
    private readonly ConcurrentDictionary<string, SessionCacheEntry> _cache = new(PathComparer);
    private readonly SemaphoreSlim _costSummaryCacheLock = new(1, 1);
    private readonly object _sessionFilesCacheLock = new();
    private IReadOnlyList<string>? _sessionRoots;
    private IReadOnlyList<string>? _wslDistroRoots;
    private CostSummaryCacheEntry? _costSummaryCache;
    private SessionFilesCacheEntry? _sessionFilesCache;

    public ViewerService(
        LabelStore labelStore,
        ViewerSettingsStore viewerSettings,
        ModelCatalogService modelCatalog,
        ExchangeRateService exchangeRates)
    {
        _labelStore = labelStore;
        _viewerSettings = viewerSettings;
        _modelCatalog = modelCatalog;
        _exchangeRates = exchangeRates;
    }

    public IReadOnlyList<string> GetSessionRoots()
    {
        if (_sessionRoots is not null)
        {
            return _sessionRoots;
        }

        var raw = Environment.GetEnvironmentVariable("SESSIONS_DIR");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            _sessionRoots = [NormalizeSessionsDir(raw)];
            return _sessionRoots;
        }

        var candidates = new List<string> { CanonicalizePath(DefaultSessionsDir) };
        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(DiscoverWindowsSessionsDirs());
            candidates.AddRange(DiscoverWslSessionsDirs());
        }
        else if (IsWsl())
        {
            candidates.AddRange(DiscoverWindowsSessionsDirsFromWsl());
        }

        var unique = UniquePaths(candidates);
        var existing = unique.Where(Directory.Exists).ToArray();
        _sessionRoots = existing.Length > 0 ? existing : unique;
        return _sessionRoots;
    }

    public async Task<LabelsResponse> GetLabelsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        return new LabelsResponse { Labels = snapshot.Labels };
    }

    public async Task<LabeledItemsResponse> GetLabeledItemsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var labeledSessions = new List<SessionSummaryDto>();
        var labeledEvents = new List<LabeledEventListItemDto>();

        foreach (var path in EnumerateSessionFiles(GetSessionRoots()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IndexRecord record;
            try
            {
                record = GetOrBuildIndexRecord(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var sessionPath = record.Summary.Path;
            if (snapshot.SessionLabels.TryGetValue(sessionPath, out var sessionLabelIds))
            {
                var labels = ResolveLabels(sessionLabelIds, snapshot.LabelById);
                if (labels.Count > 0)
                {
                    labeledSessions.Add(WithSessionLabels(record.Summary, sessionLabelIds, labels));
                }
            }

            if (snapshot.EventLabels.TryGetValue(sessionPath, out var labelsByEventId) && labelsByEventId.Count > 0)
            {
                labeledEvents.AddRange(BuildLabeledEventItems(path, record.Summary, labelsByEventId, snapshot.LabelById, cancellationToken));
            }
        }

        return new LabeledItemsResponse
        {
            Sessions = labeledSessions
                .OrderByDescending(GetSessionSortKey, StringComparer.Ordinal)
                .ThenByDescending(session => session.Mtime, StringComparer.Ordinal)
                .ToArray(),
            Events = labeledEvents
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.Timestamp) ? item.Timestamp : item.SessionStartedAt, StringComparer.Ordinal)
                .ThenByDescending(item => item.SessionMtime, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    public ModelCatalogStatusDto GetModelCatalogStatus()
    {
        return _modelCatalog.GetStatus();
    }

    public async Task<CostSummaryResponse> GetCostSummaryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryGetCachedCostSummary(out var cached))
        {
            return cached;
        }

        await _costSummaryCacheLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && TryGetCachedCostSummary(out cached))
            {
                return cached;
            }

            var response = await BuildCostSummaryAsync(cancellationToken);
            _costSummaryCache = new CostSummaryCacheEntry(DateTimeOffset.UtcNow, response);
            return response;
        }
        finally
        {
            _costSummaryCacheLock.Release();
        }
    }

    private async Task<CostSummaryResponse> BuildCostSummaryAsync(CancellationToken cancellationToken = default)
    {
        var timeZone = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime;
        var groupDefinitions = BuildCostSummaryGroupDefinitions(nowLocal);
        var groupAccumulators = groupDefinitions
            .Select(definition => new CostSummaryGroupAccumulator(definition))
            .ToArray();
        var exchangeRate = await _exchangeRates.GetUsdJpyRateAsync(cancellationToken);

        foreach (var path in EnumerateSessionFiles(GetSessionRoots()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IndexRecord indexRecord;
            try
            {
                indexRecord = GetOrBuildIndexRecord(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var sessionUsage = new TokenUsageAccumulator();
            TokenUsageSnapshot? previousTotals = null;
            var currentModel = string.Empty;
            var currentReasoningEffort = string.Empty;
            var rawLineCount = 0;

            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rawLineCount++;

                    if (!TryParseJson(line, out var root))
                    {
                        continue;
                    }

                    using (root)
                    {
                        var element = root.RootElement;
                        var type = GetString(element, "type");
                        var timestamp = GetString(element, "timestamp");
                        if (!element.TryGetProperty("payload", out var payload))
                        {
                            continue;
                        }

                        if (type == "turn_context")
                        {
                            var turnModel = ExtractModelName(payload);
                            if (!string.IsNullOrWhiteSpace(turnModel))
                            {
                                currentModel = turnModel;
                            }

                            var turnReasoningEffort = ExtractReasoningEffort(payload);
                            if (!string.IsNullOrWhiteSpace(turnReasoningEffort))
                            {
                                currentReasoningEffort = turnReasoningEffort;
                            }

                            continue;
                        }

                        if (type != "event_msg" || GetString(payload, "type") != "token_count")
                        {
                            continue;
                        }

                        var usageEvent = BuildTokenUsageEvent(
                            payload,
                            timestamp,
                            rawLineCount,
                            ref currentModel,
                            ref currentReasoningEffort,
                            ref previousTotals);
                        if (usageEvent is null)
                        {
                            continue;
                        }

                        sessionUsage.Add(usageEvent);
                        if (TryGetLocalTimestamp(usageEvent.Timestamp, timeZone, out var eventLocal))
                        {
                            foreach (var group in groupAccumulators)
                            {
                                group.AddTokenUsageEvent(eventLocal, usageEvent);
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Keep partial aggregates for readable portions of the file.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip unreadable files.
            }

            if (!sessionUsage.HasUsage)
            {
                continue;
            }

            if (!TryGetSessionAggregateTimestamp(indexRecord.Summary, timeZone, out var sessionLocal))
            {
                continue;
            }

            var sessionSummary = sessionUsage.ToDto();
            foreach (var group in groupAccumulators)
            {
                group.AddSessionUsage(sessionLocal, sessionSummary);
            }
        }

        return new CostSummaryResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            TimeZoneId = timeZone.Id,
            ExchangeRate = exchangeRate,
            Groups = groupAccumulators
                .Select(group => group.ToDto())
                .ToArray(),
        };
    }

    public async Task<SessionListResponse> GetSessionsAsync(
        string? query,
        string? mode,
        string? sort,
        int? sessionLabelId,
        int? eventLabelId,
        bool forceRefreshSessionFiles = false,
        CancellationToken cancellationToken = default)
    {
        var roots = GetSessionRoots();
        var settings = _viewerSettings.GetSnapshot();
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var normalizedMode = string.Equals(mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and";
        var normalizedSort = sort is "asc" or "updated" ? sort : "desc";
        var terms = ParseSearchQuery(query)
            .Select(NormalizeSearchText)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        var sessions = new List<SessionSummaryDto>();
        foreach (var path in EnumerateSessionFiles(roots, forceRefreshSessionFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexRecord record;
            try
            {
                record = GetOrBuildIndexRecord(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (terms.Length > 0 && !MatchesTerms(record.SearchText, terms, normalizedMode))
            {
                continue;
            }

            var sessionLabelIds = snapshot.SessionLabels.TryGetValue(record.Summary.Path, out var labelIds)
                ? labelIds
                : Array.Empty<int>();
            if (sessionLabelId.HasValue && !sessionLabelIds.Contains(sessionLabelId.Value))
            {
                continue;
            }

            if (eventLabelId.HasValue && !HasEventLabel(snapshot, record.Summary.Path, eventLabelId.Value))
            {
                continue;
            }

            sessions.Add(WithSessionLabelIds(record.Summary, sessionLabelIds));
        }

        IOrderedEnumerable<SessionSummaryDto> ordered = normalizedSort switch
        {
            "asc" => sessions
                .OrderBy(GetSessionSortKey, StringComparer.Ordinal)
                .ThenBy(session => session.Mtime, StringComparer.Ordinal),
            "updated" => sessions
                .OrderByDescending(session => session.Mtime, StringComparer.Ordinal)
                .ThenByDescending(GetSessionSortKey, StringComparer.Ordinal),
            _ => sessions
                .OrderByDescending(GetSessionSortKey, StringComparer.Ordinal)
                .ThenByDescending(session => session.Mtime, StringComparer.Ordinal),
        };

        var limitedSessions = ordered.Take(settings.SessionListMax).ToArray();
        return new SessionListResponse
        {
            Root = string.Join(" | ", roots),
            Sessions = limitedSessions,
            TotalCount = limitedSessions.Length,
            Offset = 0,
            Limit = settings.SessionListMax,
            HasMore = false,
        };
    }

    public async Task<SessionListResponse> GetSessionsLiteAsync(
        string? sort,
        int? offset,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var roots = GetSessionRoots();
        var settings = _viewerSettings.GetSnapshot();
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var normalizedSort = sort is "asc" or "updated" ? sort : "desc";
        var allPaths = EnumerateSessionFiles(roots).ToArray();
        if (normalizedSort == "asc")
        {
            Array.Reverse(allPaths);
        }

        var limitedPaths = allPaths.Take(settings.SessionListMax).ToArray();
        var totalCount = limitedPaths.Length;
        var normalizedOffset = Math.Clamp(offset ?? 0, 0, totalCount);
        var normalizedLimit = Math.Clamp(limit ?? settings.SessionListInitialLoadCount, 1, settings.SessionListMax);
        var pagePaths = limitedPaths
            .Skip(normalizedOffset)
            .Take(normalizedLimit)
            .ToArray();

        var sessions = new List<SessionSummaryDto>(pagePaths.Length);
        foreach (var path in pagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IndexRecord record;
            try
            {
                record = GetOrBuildIndexRecord(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var sessionLabelIds = snapshot.SessionLabels.TryGetValue(record.Summary.Path, out var labelIds)
                ? labelIds
                : Array.Empty<int>();
            sessions.Add(WithSessionLabelIds(record.Summary, sessionLabelIds));
        }

        return new SessionListResponse
        {
            Root = string.Join(" | ", roots),
            Sessions = sessions,
            TotalCount = totalCount,
            Offset = normalizedOffset,
            Limit = normalizedLimit,
            HasMore = normalizedOffset + sessions.Count < totalCount,
        };
    }

    public async Task<SessionDetailResponse> GetSessionAsync(string? rawPath, bool includeEvents, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(rawPath);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("session file not found");
        }

        var sessionVersion = BuildSessionVersion(fileInfo);
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var indexRecord = GetOrBuildIndexRecord(path);
        var sessionPath = indexRecord.Summary.Path;
        var sessionLabelIds = snapshot.SessionLabels.TryGetValue(sessionPath, out var sIds) ? sIds : Array.Empty<int>();
        var exchangeRate = await _exchangeRates.GetUsdJpyRateAsync(cancellationToken);

        if (!includeEvents)
        {
            return new SessionDetailResponse
            {
                Session = WithSessionLabels(
                    indexRecord.Summary,
                    sessionLabelIds,
                    ResolveLabels(sessionLabelIds, snapshot.LabelById)),
                SessionVersion = sessionVersion,
                ExchangeRate = exchangeRate,
            };
        }

        var eventsData = GetOrBuildEvents(path);
        var labelsByEvent = snapshot.EventLabels.TryGetValue(sessionPath, out var eventMap)
            ? eventMap
            : null;

        return new SessionDetailResponse
        {
            Session = WithSessionLabels(
                indexRecord.Summary,
                sessionLabelIds,
                ResolveLabels(sessionLabelIds, snapshot.LabelById)),
            SessionVersion = sessionVersion,
            Events = eventsData.Events
                .Select(@event => WithEventLabels(
                    @event,
                    ResolveLabels(
                        labelsByEvent is not null && labelsByEvent.TryGetValue(@event.EventId, out var ids)
                            ? ids
                            : Array.Empty<int>(),
                        snapshot.LabelById)))
                .ToArray(),
            RawLineCount = eventsData.RawLineCount,
            ExchangeRate = exchangeRate,
            Usage = eventsData.Usage,
        };
    }

    public SessionVersionResponse GetSessionVersion(string? rawPath)
    {
        var path = ResolveSessionPath(rawPath);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("session file not found");
        }

        return new SessionVersionResponse
        {
            Path = path,
            SessionVersion = BuildSessionVersion(fileInfo),
        };
    }

    public string ResolveSessionPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidOperationException("path is required");
        }

        var candidate = CanonicalizePath(rawPath);
        foreach (var root in GetSessionRoots())
        {
            if (IsWithinRoot(candidate, root))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("path is outside sessions directory");
    }

    public async Task<LabelDto> SaveLabelAsync(SaveLabelRequest request, CancellationToken cancellationToken = default)
    {
        return await _labelStore.SaveLabelAsync(request.Id, request.Name, request.ColorValue, request.ColorFamily, cancellationToken);
    }

    public async Task DeleteLabelAsync(int id, CancellationToken cancellationToken = default)
    {
        await _labelStore.DeleteLabelAsync(id, cancellationToken);
    }

    public async Task AddSessionLabelAsync(SessionLabelMutationRequest request, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(request.Path);
        if (request.LabelId is null)
        {
            throw new InvalidOperationException("label id is required");
        }

        await _labelStore.AddSessionLabelAsync(path, request.LabelId.Value, cancellationToken);
    }

    public async Task RemoveSessionLabelAsync(SessionLabelMutationRequest request, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(request.Path);
        if (request.LabelId is null)
        {
            throw new InvalidOperationException("label id is required");
        }

        await _labelStore.RemoveSessionLabelAsync(path, request.LabelId.Value, cancellationToken);
    }

    public async Task AddEventLabelAsync(EventLabelMutationRequest request, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(request.Path);
        if (request.LabelId is null || string.IsNullOrWhiteSpace(request.EventId))
        {
            throw new InvalidOperationException("label id and event id are required");
        }

        await _labelStore.AddEventLabelAsync(path, request.EventId.Trim(), request.LabelId.Value, cancellationToken);
    }

    public async Task RemoveEventLabelAsync(EventLabelMutationRequest request, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(request.Path);
        if (request.LabelId is null || string.IsNullOrWhiteSpace(request.EventId))
        {
            throw new InvalidOperationException("label id and event id are required");
        }

        await _labelStore.RemoveEventLabelAsync(path, request.EventId.Trim(), request.LabelId.Value, cancellationToken);
    }

    private static bool HasEventLabel(LabelStoreSnapshot snapshot, string path, int labelId)
    {
        return snapshot.EventLabels.TryGetValue(path, out var eventMap)
            && eventMap.Values.Any(labelIds => labelIds.Contains(labelId));
    }

    private IEnumerable<LabeledEventListItemDto> BuildLabeledEventItems(
        string path,
        SessionSummaryDto session,
        IReadOnlyDictionary<string, IReadOnlyList<int>> labelsByEventId,
        IReadOnlyDictionary<int, LabelDto> labelById,
        CancellationToken cancellationToken)
    {
        if (labelsByEventId.Count == 0)
        {
            yield break;
        }

        var targetEventIds = labelsByEventId.Keys
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (targetEventIds.Count == 0)
        {
            yield break;
        }

        TokenUsageSnapshot? previousTotals = null;
        var currentModel = string.Empty;
        var currentReasoningEffort = string.Empty;
        var rawLineCount = 0;

        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rawLineCount++;

            if (!TryParseJson(line, out var root))
            {
                continue;
            }

            using (root)
            {
                var element = root.RootElement;
                var type = GetString(element, "type");
                var timestamp = GetString(element, "timestamp");
                if (!element.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                if (type == "turn_context")
                {
                    var turnModel = ExtractModelName(payload);
                    if (!string.IsNullOrWhiteSpace(turnModel))
                    {
                        currentModel = turnModel;
                    }

                    var turnReasoningEffort = ExtractReasoningEffort(payload);
                    if (!string.IsNullOrWhiteSpace(turnReasoningEffort))
                    {
                        currentReasoningEffort = turnReasoningEffort;
                    }

                    continue;
                }

                var eventId = $"line-{rawLineCount}";

                if (type == "event_msg" && GetString(payload, "type") == "token_count")
                {
                    var usageEvent = BuildTokenUsageEvent(
                        payload,
                        timestamp,
                        rawLineCount,
                        ref currentModel,
                        ref currentReasoningEffort,
                        ref previousTotals);
                    if (usageEvent is null || !targetEventIds.Contains(eventId))
                    {
                        continue;
                    }

                    var labels = ResolveLabels(labelsByEventId[eventId], labelById);
                    if (labels.Count == 0)
                    {
                        continue;
                    }

                    yield return ToLabeledEventItem(session, usageEvent, labels);
                    continue;
                }

                if (!targetEventIds.Contains(eventId))
                {
                    continue;
                }

                SessionEventDto? @event = null;
                if (type == "response_item")
                {
                    var responseType = GetString(payload, "type");
                    if (responseType == "message")
                    {
                        var role = GetString(payload, "role");
                        var text = ExtractTextFromContent(payload);
                        var systemLabels = Array.Empty<string>();
                        if (role == "user")
                        {
                            role = ClassifyUserMessage(text);
                            systemLabels = DetectUserMessageSystemLabels(text);
                        }

                        @event = new SessionEventDto
                        {
                            EventId = eventId,
                            Timestamp = timestamp,
                            Kind = "message",
                            Role = role,
                            Text = text,
                            SystemLabels = systemLabels,
                        };
                    }
                    else if (responseType == "function_call")
                    {
                        @event = new SessionEventDto
                        {
                            EventId = eventId,
                            Timestamp = timestamp,
                            Kind = "function_call",
                            Name = GetString(payload, "name"),
                            Arguments = GetValueText(payload, "arguments"),
                        };
                    }
                    else if (responseType == "function_call_output")
                    {
                        @event = new SessionEventDto
                        {
                            EventId = eventId,
                            Timestamp = timestamp,
                            Kind = "function_output",
                            CallId = GetString(payload, "call_id"),
                            Output = GetValueText(payload, "output"),
                        };
                    }
                }
                else if (type == "event_msg" && GetString(payload, "type") == "agent_message")
                {
                    @event = new SessionEventDto
                    {
                        EventId = eventId,
                        Timestamp = timestamp,
                        Kind = "agent_update",
                        Text = GetValueText(payload, "message"),
                    };
                }

                if (@event is null)
                {
                    continue;
                }

                var resolvedLabels = ResolveLabels(labelsByEventId[eventId], labelById);
                if (resolvedLabels.Count == 0)
                {
                    continue;
                }

                yield return ToLabeledEventItem(session, @event, resolvedLabels);
            }
        }
    }

    private static IReadOnlyList<LabelDto> ResolveLabels(IEnumerable<int> ids, IReadOnlyDictionary<int, LabelDto> labelById)
    {
        return ids
            .Distinct()
            .Select(id => labelById.TryGetValue(id, out var label) ? label : null)
            .Where(label => label is not null)
            .Cast<LabelDto>()
            .OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(label => label.Id)
            .ToArray();
    }

    private static string GetSessionSortKey(SessionSummaryDto session)
    {
        return !string.IsNullOrWhiteSpace(session.StartedAt) ? session.StartedAt : session.Mtime;
    }

    private static SessionSummaryDto WithSessionLabels(SessionSummaryDto session, IReadOnlyList<int> labelIds, IReadOnlyList<LabelDto> labels)
    {
        return session with { SessionLabelIds = labelIds, SessionLabels = labels };
    }

    private static LabeledEventListItemDto ToLabeledEventItem(
        SessionSummaryDto session,
        SessionEventDto @event,
        IReadOnlyList<LabelDto> labels)
    {
        return new LabeledEventListItemDto
        {
            Path = session.Path,
            RelativePath = session.RelativePath,
            SessionId = session.SessionId,
            SessionStartedAt = session.StartedAt,
            SessionMtime = session.Mtime,
            Cwd = session.Cwd,
            Source = session.Source,
            EventId = @event.EventId,
            Timestamp = @event.Timestamp,
            Kind = @event.Kind,
            Role = @event.Role,
            Preview = BuildLabeledEventPreview(@event),
            Labels = labels,
        };
    }

    private static string BuildLabeledEventPreview(SessionEventDto @event)
    {
        var text = @event.Kind switch
        {
            "message" => @event.Text,
            "function_call" => string.Join(' ', new[] { @event.Name, @event.Arguments }.Where(value => !string.IsNullOrWhiteSpace(value))),
            "function_output" => @event.Output,
            "agent_update" => @event.Text,
            "token_usage" => BuildTokenUsagePreview(@event),
            _ => string.Join(' ', new[] { @event.Text, @event.Output, @event.Arguments }.Where(value => !string.IsNullOrWhiteSpace(value))),
        };

        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : CollapseNewlines(text, 220);
    }

    private static string BuildTokenUsagePreview(SessionEventDto @event)
    {
        var parts = new List<string>
        {
            $"total {@event.TotalTokens.ToString("N0", CultureInfo.InvariantCulture)}"
        };
        if (@event.CachedInputTokens > 0)
        {
            parts.Add($"cache {@event.CachedInputTokens.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (@event.CostUsd.HasValue)
        {
            parts.Add($"cost ${@event.CostUsd.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        return string.Join(" / ", parts);
    }

    private static IReadOnlyList<CostSummaryGroupDefinition> BuildCostSummaryGroupDefinitions(DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var thisMonthStart = new DateTime(today.Year, today.Month, 1);
        var thisWeekStart = StartOfWeek(today, DayOfWeek.Monday);

        return
        [
            new CostSummaryGroupDefinition(
                "month",
                [
                    new CostSummaryPeriodDefinition("two_months_ago", thisMonthStart.AddMonths(-2), thisMonthStart.AddMonths(-1)),
                    new CostSummaryPeriodDefinition("last_month", thisMonthStart.AddMonths(-1), thisMonthStart),
                    new CostSummaryPeriodDefinition("this_month", thisMonthStart, thisMonthStart.AddMonths(1)),
                ]),
            new CostSummaryGroupDefinition(
                "week",
                [
                    new CostSummaryPeriodDefinition("two_weeks_ago", thisWeekStart.AddDays(-14), thisWeekStart.AddDays(-7)),
                    new CostSummaryPeriodDefinition("last_week", thisWeekStart.AddDays(-7), thisWeekStart),
                    new CostSummaryPeriodDefinition("this_week", thisWeekStart, thisWeekStart.AddDays(7)),
                ]),
            new CostSummaryGroupDefinition(
                "day",
                [
                    new CostSummaryPeriodDefinition("two_days_ago", today.AddDays(-2), today.AddDays(-1)),
                    new CostSummaryPeriodDefinition("yesterday", today.AddDays(-1), today),
                    new CostSummaryPeriodDefinition("today", today, today.AddDays(1)),
                ]),
        ];
    }

    private static DateTime StartOfWeek(DateTime date, DayOfWeek weekStartsOn)
    {
        var normalized = date.Date;
        var diff = (7 + (normalized.DayOfWeek - weekStartsOn)) % 7;
        return normalized.AddDays(-diff);
    }

    private static bool TryGetSessionAggregateTimestamp(SessionSummaryDto summary, TimeZoneInfo timeZone, out DateTime localTimestamp)
    {
        var candidate = !string.IsNullOrWhiteSpace(summary.StartedAt)
            ? summary.StartedAt
            : !string.IsNullOrWhiteSpace(summary.MaxEventTs)
                ? summary.MaxEventTs
                : summary.Mtime;
        return TryGetLocalTimestamp(candidate, timeZone, out localTimestamp);
    }

    private static bool TryGetLocalTimestamp(string? rawTimestamp, TimeZoneInfo timeZone, out DateTime localTimestamp)
    {
        if (TryParseTimestamp(rawTimestamp, out var parsed))
        {
            localTimestamp = TimeZoneInfo.ConvertTime(parsed, timeZone).DateTime;
            return true;
        }

        localTimestamp = default;
        return false;
    }

    private static bool TryParseTimestamp(string? rawTimestamp, out DateTimeOffset parsed)
    {
        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            parsed = default;
            return false;
        }

        return DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed)
            || DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed);
    }

    private static SessionSummaryDto WithSessionLabelIds(SessionSummaryDto session, IReadOnlyList<int> labelIds)
    {
        return session with { SessionLabelIds = labelIds };
    }

    private static SessionEventDto WithEventLabels(SessionEventDto @event, IReadOnlyList<LabelDto> labels)
    {
        return new SessionEventDto
        {
            EventId = @event.EventId,
            Timestamp = @event.Timestamp,
            Kind = @event.Kind,
            Role = @event.Role,
            Text = @event.Text,
            Name = @event.Name,
            Arguments = @event.Arguments,
            CallId = @event.CallId,
            Output = @event.Output,
            Model = @event.Model,
            InputTokens = @event.InputTokens,
            CachedInputTokens = @event.CachedInputTokens,
            OutputTokens = @event.OutputTokens,
            ReasoningOutputTokens = @event.ReasoningOutputTokens,
            TotalTokens = @event.TotalTokens,
            CostUsd = @event.CostUsd,
            SystemLabels = @event.SystemLabels,
            Labels = labels,
        };
    }

    private IndexRecord GetOrBuildIndexRecord(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            _cache.TryRemove(path, out _);
            throw new FileNotFoundException("session file not found", path);
        }

        var signature = GetSignature(fileInfo);
        if (_cache.TryGetValue(path, out var cached)
            && cached.Signature == signature
            && cached.IndexRecord is not null)
        {
            cached.LastAccessedTicks = Environment.TickCount64;
            return cached.IndexRecord;
        }

        var built = BuildIndexRecord(path, fileInfo);
        var next = new SessionCacheEntry
        {
            Signature = signature,
            IndexRecord = built,
            EventsData = cached is not null && cached.Signature == signature ? cached.EventsData : null,
            PricingVersion = cached?.PricingVersion ?? 0,
            ViewerSettingsVersion = cached?.ViewerSettingsVersion ?? 0,
            MaxEvents = cached?.MaxEvents ?? 0,
        };
        _cache[path] = next;
        TrimCacheIfNeeded();
        return built;
    }

    private EventsData GetOrBuildEvents(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            _cache.TryRemove(path, out _);
            throw new FileNotFoundException("session file not found", path);
        }

        var signature = GetSignature(fileInfo);
        var pricingVersion = _modelCatalog.GetPricingVersion();
        var settings = _viewerSettings.GetSnapshot();
        if (_cache.TryGetValue(path, out var cached)
            && cached.Signature == signature
            && cached.PricingVersion == pricingVersion
            && cached.ViewerSettingsVersion == settings.Version
            && cached.MaxEvents == settings.SessionEventsMax
            && cached.EventsData is not null)
        {
            cached.LastAccessedTicks = Environment.TickCount64;
            return cached.EventsData;
        }

        var built = BuildEventsData(path, settings.SessionEventsMax);
        var next = new SessionCacheEntry
        {
            Signature = signature,
            IndexRecord = cached is not null && cached.Signature == signature ? cached.IndexRecord : null,
            EventsData = built,
            PricingVersion = pricingVersion,
            ViewerSettingsVersion = settings.Version,
            MaxEvents = settings.SessionEventsMax,
        };
        _cache[path] = next;
        TrimCacheIfNeeded();
        return built;
    }

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count <= MaxCacheEntries)
        {
            return;
        }

        var entries = _cache.ToArray();
        var scored = entries
            .Select(pair => (pair.Key, Ticks: pair.Value.LastAccessedTicks))
            .OrderBy(item => item.Ticks)
            .Take(entries.Length - MaxCacheEntries)
            .ToArray();

        foreach (var item in scored)
        {
            _cache.TryRemove(item.Key, out _);
        }
    }

    private bool TryGetCachedCostSummary(out CostSummaryResponse response)
    {
        var cached = _costSummaryCache;
        if (cached is not null && DateTimeOffset.UtcNow - cached.BuiltAtUtc <= CostSummaryCacheTtl)
        {
            response = cached.Response;
            return true;
        }

        response = null!;
        return false;
    }

    private SessionSummaryDto? TryGetCachedSummary(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            _cache.TryRemove(path, out _);
            throw new FileNotFoundException("session file not found", path);
        }

        var signature = GetSignature(fileInfo);
        if (_cache.TryGetValue(path, out var cached)
            && cached.Signature == signature
            && cached.IndexRecord is not null)
        {
            cached.LastAccessedTicks = Environment.TickCount64;
            return cached.IndexRecord.Summary;
        }

        return null;
    }

    private SessionSummaryDto BuildLiteSummary(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("session file not found", path);
        }

        var summary = new SessionSummaryDto
        {
            Id = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = CanonicalizePath(path),
            RelativePath = ToRelativePath(path),
            Mtime = fileInfo.LastWriteTime.ToString("s"),
            SessionId = string.Empty,
            StartedAt = string.Empty,
            Cwd = string.Empty,
            Model = string.Empty,
            ReasoningEffort = string.Empty,
            Source = "cli",
            IsSubagent = false,
            FirstUserText = string.Empty,
            FirstRealUserText = string.Empty,
            MinEventTs = string.Empty,
            MaxEventTs = string.Empty,
        };

        try
        {
            foreach (var line in File.ReadLines(path).Take(8))
            {
                if (!TryParseJson(line, out var root))
                {
                    continue;
                }

                using (root)
                {
                    var element = root.RootElement;
                    if (GetString(element, "type") != "session_meta" || !element.TryGetProperty("payload", out var payload))
                    {
                        continue;
                    }

                    var rawSource = GetString(payload, "source");
                    var originator = GetString(payload, "originator");
                    summary = summary with
                    {
                        SessionId = GetString(payload, "id"),
                        StartedAt = GetString(payload, "timestamp"),
                        Cwd = GetString(payload, "cwd"),
                        Model = GetString(payload, "model_provider"),
                        Source = ClassifySource(rawSource, originator),
                        IsSubagent = IsSubagentSource(rawSource, originator),
                    };
                    break;
                }
            }
        }
        catch
        {
            // Keep partial summary if the file is unreadable.
        }

        return summary;
    }

    private IndexRecord BuildIndexRecord(string path, FileInfo fileInfo)
    {
        var summary = new SessionSummaryDto
        {
            Id = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = CanonicalizePath(path),
            RelativePath = ToRelativePath(path),
            Mtime = fileInfo.LastWriteTime.ToString("s"),
            SessionId = string.Empty,
            StartedAt = string.Empty,
            Cwd = string.Empty,
            Model = string.Empty,
            ReasoningEffort = string.Empty,
            Source = "cli",
            IsSubagent = false,
            FirstUserText = string.Empty,
            FirstRealUserText = string.Empty,
            MinEventTs = string.Empty,
            MaxEventTs = string.Empty,
        };

        var searchChunks = new List<string>();
        var searchLength = 0;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!TryParseJson(line, out var root))
                {
                    continue;
                }

                using (root)
                {
                    var element = root.RootElement;
                    var type = GetString(element, "type");
                    var timestamp = GetString(element, "timestamp");
                    UpdateMinMaxEventTimestamps(ref summary, timestamp);

                    if (!element.TryGetProperty("payload", out var payload))
                    {
                        continue;
                    }

                    switch (type)
                    {
                        case "session_meta":
                            summary = summary with
                            {
                                SessionId = GetString(payload, "id"),
                                StartedAt = GetString(payload, "timestamp"),
                                Cwd = GetString(payload, "cwd"),
                                Model = GetString(payload, "model_provider"),
                                Source = ClassifySource(GetString(payload, "source"), GetString(payload, "originator")),
                                IsSubagent = IsSubagentSource(GetString(payload, "source"), GetString(payload, "originator")),
                            };
                            break;
                        case "turn_context":
                            summary = UpdateSummaryFromTurnContext(summary, payload);
                            break;
                        case "response_item":
                            searchLength = AppendResponseItemSearchText(payload, summary.Source, searchChunks, searchLength);
                            summary = UpdateSummaryFromResponseItem(summary, payload);
                            break;
                        case "event_msg":
                            if (GetString(payload, "type") == "agent_message")
                            {
                                searchLength = AppendSearchChunk(searchChunks, GetValueText(payload, "message"), searchLength, SearchTextLimit);
                            }

                            break;
                    }
                }
            }
        }
        catch
        {
            // Keep partial summary if the file is unreadable.
        }

        if (string.IsNullOrWhiteSpace(summary.FirstRealUserText))
        {
            summary = summary with { FirstRealUserText = summary.FirstUserText };
        }

        var searchPrefix = new[]
        {
            summary.RelativePath,
            summary.Cwd,
            summary.SessionId,
            summary.Source,
            summary.FirstUserText,
            summary.FirstRealUserText,
        };
        var normalizedPrefix = searchPrefix
            .Select(NormalizeSearchText)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var searchText = string.Join(' ', normalizedPrefix.Concat(searchChunks));
        return new IndexRecord(summary, searchText);
    }

    private EventsData BuildEventsData(string path, int maxEvents)
    {
        var events = new List<SessionEventDto>();
        var rawLineCount = 0;
        var usageAccumulator = new TokenUsageAccumulator();
        TokenUsageSnapshot? previousTotals = null;
        var currentModel = string.Empty;
        var currentReasoningEffort = string.Empty;
        foreach (var line in File.ReadLines(path))
        {
            rawLineCount++;
            if (!TryParseJson(line, out var root))
            {
                continue;
            }

            using (root)
            {
                var element = root.RootElement;
                var type = GetString(element, "type");
                var timestamp = GetString(element, "timestamp");
                if (!element.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                if (type == "turn_context")
                {
                    var turnModel = ExtractModelName(payload);
                    if (!string.IsNullOrWhiteSpace(turnModel))
                    {
                        currentModel = turnModel;
                    }

                    var turnReasoningEffort = ExtractReasoningEffort(payload);
                    if (!string.IsNullOrWhiteSpace(turnReasoningEffort))
                    {
                        currentReasoningEffort = turnReasoningEffort;
                    }
                }
                else if (type == "response_item")
                {
                    var responseType = GetString(payload, "type");
                    if (responseType == "message")
                    {
                        var role = GetString(payload, "role");
                        var text = ExtractTextFromContent(payload);
                        if (!string.IsNullOrWhiteSpace(text) && events.Count < maxEvents)
                        {
                            var systemLabels = Array.Empty<string>();
                            if (role == "user")
                            {
                                role = ClassifyUserMessage(text);
                                systemLabels = DetectUserMessageSystemLabels(text);
                            }

                            events.Add(new SessionEventDto
                            {
                                EventId = $"line-{rawLineCount}",
                                Timestamp = timestamp,
                                Kind = "message",
                                Role = role,
                                Text = text,
                                SystemLabels = systemLabels,
                            });
                        }
                    }
                    else if (responseType == "function_call" && events.Count < maxEvents)
                    {
                        events.Add(new SessionEventDto
                        {
                            EventId = $"line-{rawLineCount}",
                            Timestamp = timestamp,
                            Kind = "function_call",
                            Name = GetString(payload, "name"),
                            Arguments = GetValueText(payload, "arguments"),
                        });
                    }
                    else if (responseType == "function_call_output" && events.Count < maxEvents)
                    {
                        events.Add(new SessionEventDto
                        {
                            EventId = $"line-{rawLineCount}",
                            Timestamp = timestamp,
                            Kind = "function_output",
                            CallId = GetString(payload, "call_id"),
                            Output = GetValueText(payload, "output"),
                        });
                    }
                }
                else if (type == "event_msg")
                {
                    var eventType = GetString(payload, "type");
                    if (eventType == "agent_message" && events.Count < maxEvents)
                    {
                        events.Add(new SessionEventDto
                        {
                            EventId = $"line-{rawLineCount}",
                            Timestamp = timestamp,
                            Kind = "agent_update",
                            Text = GetValueText(payload, "message"),
                        });
                    }
                    else if (eventType == "token_count")
                    {
                        var usageEvent = BuildTokenUsageEvent(
                            payload,
                            timestamp,
                            rawLineCount,
                            ref currentModel,
                            ref currentReasoningEffort,
                            ref previousTotals);
                        if (usageEvent is not null)
                        {
                            usageAccumulator.Add(usageEvent);
                            if (events.Count < maxEvents)
                            {
                                events.Add(usageEvent);
                            }
                        }
                    }
                }
            }
        }

        return new EventsData(
            events,
            rawLineCount,
            usageAccumulator.HasUsage ? usageAccumulator.ToDto() : null);
    }

    private SessionEventDto? BuildTokenUsageEvent(
        JsonElement payload,
        string timestamp,
        int rawLineCount,
        ref string currentModel,
        ref string currentReasoningEffort,
        ref TokenUsageSnapshot? previousTotals)
    {
        var extractedModel = ExtractModelName(payload);
        if (!string.IsNullOrWhiteSpace(extractedModel))
        {
            currentModel = extractedModel;
        }

        var extractedReasoningEffort = ExtractReasoningEffort(payload);
        if (!string.IsNullOrWhiteSpace(extractedReasoningEffort))
        {
            currentReasoningEffort = extractedReasoningEffort;
        }

        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var totalUsage = TryGetTokenUsageSnapshot(info, "total_token_usage");
        var lastUsage = TryGetTokenUsageSnapshot(info, "last_token_usage");
        var delta = totalUsage is not null
            ? SubtractTokenUsage(totalUsage.Value, previousTotals)
            : lastUsage;

        if (totalUsage is not null)
        {
            previousTotals = totalUsage;
        }

        if (delta is null)
        {
            return null;
        }

        var normalized = NormalizeTokenUsage(delta.Value);
        if (normalized.IsEmpty)
        {
            return null;
        }

        var costUsd = _modelCatalog.TryCalculateCostUsd(
            currentModel,
            normalized.InputTokens,
            normalized.CachedInputTokens,
            normalized.OutputTokens);
        return new SessionEventDto
        {
            EventId = $"line-{rawLineCount}",
            Timestamp = timestamp,
            Kind = "token_usage",
            Role = "system",
            Model = currentModel,
            ReasoningEffort = currentReasoningEffort,
            InputTokens = normalized.InputTokens,
            CachedInputTokens = normalized.CachedInputTokens,
            OutputTokens = normalized.OutputTokens,
            ReasoningOutputTokens = normalized.ReasoningOutputTokens,
            TotalTokens = normalized.TotalTokens,
            CostUsd = costUsd,
        };
    }

    private static string ExtractModelName(JsonElement payload)
    {
        foreach (var candidate in new[]
        {
            GetString(payload, "model"),
            GetString(payload, "model_name"),
            TryGetNestedString(payload, "info", "model"),
            TryGetNestedString(payload, "info", "model_name"),
            TryGetNestedString(payload, "info", "metadata", "model"),
            TryGetNestedString(payload, "metadata", "model"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractReasoningEffort(JsonElement payload)
    {
        foreach (var candidate in new[]
        {
            GetString(payload, "effort"),
            GetString(payload, "reasoning_effort"),
            TryGetNestedString(payload, "collaboration_mode", "settings", "reasoning_effort"),
            TryGetNestedString(payload, "collaboration_mode", "settings", "effort"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static string TryGetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return string.Empty;
            }
        }

        if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString() ?? string.Empty
            : current.ToString();
    }

    private static TokenUsageSnapshot? TryGetTokenUsageSnapshot(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = GetInt64(usage, "input_tokens");
        var cachedInputTokens = GetInt64(usage, "cached_input_tokens");
        if (cachedInputTokens == 0)
        {
            cachedInputTokens = GetInt64(usage, "cache_read_input_tokens");
        }

        var outputTokens = GetInt64(usage, "output_tokens");
        var reasoningOutputTokens = GetInt64(usage, "reasoning_output_tokens");
        var totalTokens = GetInt64(usage, "total_tokens");

        return new TokenUsageSnapshot(
            inputTokens,
            cachedInputTokens,
            outputTokens,
            reasoningOutputTokens,
            totalTokens);
    }

    private static TokenUsageSnapshot SubtractTokenUsage(TokenUsageSnapshot current, TokenUsageSnapshot? previous)
    {
        return new TokenUsageSnapshot(
            Math.Max(current.InputTokens - previous.GetValueOrDefault().InputTokens, 0),
            Math.Max(current.CachedInputTokens - previous.GetValueOrDefault().CachedInputTokens, 0),
            Math.Max(current.OutputTokens - previous.GetValueOrDefault().OutputTokens, 0),
            Math.Max(current.ReasoningOutputTokens - previous.GetValueOrDefault().ReasoningOutputTokens, 0),
            Math.Max(current.TotalTokens - previous.GetValueOrDefault().TotalTokens, 0));
    }

    private static TokenUsageSnapshot NormalizeTokenUsage(TokenUsageSnapshot usage)
    {
        var inputTokens = Math.Max(usage.InputTokens, 0);
        var cachedInputTokens = Math.Clamp(usage.CachedInputTokens, 0, inputTokens);
        var outputTokens = Math.Max(usage.OutputTokens, 0);
        var reasoningOutputTokens = Math.Max(usage.ReasoningOutputTokens, 0);
        var totalTokens = usage.TotalTokens > 0
            ? usage.TotalTokens
            : inputTokens + outputTokens;

        return new TokenUsageSnapshot(
            inputTokens,
            cachedInputTokens,
            outputTokens,
            reasoningOutputTokens,
            Math.Max(totalTokens, 0));
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt64(out var value) ? value : 0;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static SessionSummaryDto UpdateSummaryFromResponseItem(SessionSummaryDto summary, JsonElement payload)
    {
        if (GetString(payload, "type") != "message")
        {
            return summary;
        }

        if (GetString(payload, "role") != "user")
        {
            return summary;
        }

        var contentItems = GetContentTextItems(payload);
        var firstChunk = contentItems.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(firstChunk))
        {
            return summary;
        }

        var next = summary;
        if (string.IsNullOrWhiteSpace(next.FirstUserText))
        {
            next = next with { FirstUserText = CollapseNewlines(firstChunk, 120) };
        }

        if (string.IsNullOrWhiteSpace(next.FirstRealUserText) && ClassifyUserMessage(firstChunk) == "user")
        {
            next = next with { FirstRealUserText = CollapseNewlines(firstChunk, 160) };
        }

        return next;
    }

    private static SessionSummaryDto UpdateSummaryFromTurnContext(SessionSummaryDto summary, JsonElement payload)
    {
        var reasoningEffort = ExtractReasoningEffort(payload);
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return summary;
        }

        return summary with { ReasoningEffort = reasoningEffort };
    }

    private static int AppendResponseItemSearchText(JsonElement payload, string source, List<string> searchChunks, int currentLength)
    {
        var responseType = GetString(payload, "type");
        return responseType switch
        {
            "message" => AppendSearchChunk(searchChunks, ExtractTextFromContent(payload), currentLength, SearchTextLimit),
            "function_call" => AppendSearchChunk(
                searchChunks,
                string.Join(' ',
                    new[]
                    {
                        GetValueText(payload, "name"),
                        GetValueText(payload, "arguments"),
                    }.Where(value => !string.IsNullOrWhiteSpace(value))),
                currentLength,
                SearchTextLimit),
            "function_call_output" => AppendSearchChunk(searchChunks, GetValueText(payload, "output"), currentLength, SearchTextLimit),
            _ => currentLength,
        };
    }

    private static int AppendSearchChunk(List<string> chunks, string text, int currentLength, int limit)
    {
        var normalized = NormalizeSearchText(text);
        if (string.IsNullOrWhiteSpace(normalized) || currentLength >= limit)
        {
            return currentLength;
        }

        var remaining = limit - currentLength;
        if (normalized.Length > remaining)
        {
            normalized = normalized[..remaining];
        }

        chunks.Add(normalized);
        return currentLength + normalized.Length;
    }

    private static void UpdateMinMaxEventTimestamps(ref SessionSummaryDto summary, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return;
        }

        var min = string.IsNullOrWhiteSpace(summary.MinEventTs) || string.CompareOrdinal(timestamp, summary.MinEventTs) < 0
            ? timestamp
            : summary.MinEventTs;
        var max = string.IsNullOrWhiteSpace(summary.MaxEventTs) || string.CompareOrdinal(timestamp, summary.MaxEventTs) > 0
            ? timestamp
            : summary.MaxEventTs;
        summary = summary with { MinEventTs = min, MaxEventTs = max };
    }

    private IEnumerable<string> EnumerateSessionFiles(IEnumerable<string> roots, bool forceRefresh = false)
    {
        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(CanonicalizePath)
            .Distinct(PathComparer)
            .OrderBy(root => root, PathComparer)
            .ToArray();
        var cacheKey = string.Join("|", normalizedRoots);
        var now = DateTime.UtcNow;

        lock (_sessionFilesCacheLock)
        {
            if (!forceRefresh
                && _sessionFilesCache is not null
                && _sessionFilesCache.RootsKey == cacheKey
                && now - _sessionFilesCache.BuiltAtUtc <= SessionFilesCacheTtl)
            {
                return _sessionFilesCache.Paths;
            }
        }

        var files = new Dictionary<string, FileInfo>(PathComparer);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var root in normalizedRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in SafeEnumerateFiles(root, "*.jsonl", options))
            {
                var canonical = CanonicalizePath(file);
                if (!files.ContainsKey(canonical))
                {
                    files[canonical] = new FileInfo(canonical);
                }
            }
        }

        var result = files.Values
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, PathComparer)
            .Select(file => file.FullName)
            .ToArray();

        lock (_sessionFilesCacheLock)
        {
            _sessionFilesCache = new SessionFilesCacheEntry
            {
                RootsKey = cacheKey,
                BuiltAtUtc = now,
                Paths = result,
            };
        }

        return result;
    }

    private string ToRelativePath(string path)
    {
        var canonicalPath = CanonicalizePath(path);
        foreach (var root in GetSessionRoots())
        {
            if (IsWithinRoot(canonicalPath, root))
            {
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return canonicalPath[prefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        return canonicalPath;
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, PathComparison);
    }

    private static string[] UniquePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
    }

    private static bool IsWsl()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
        {
            return true;
        }

        try
        {
            return File.Exists("/proc/version")
                && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<string> DiscoverWindowsSessionsDirs()
    {
        var candidates = new List<string>();
        foreach (var envName in new[] { "USERNAME", "WIN_USERNAME" })
        {
            var value = Environment.GetEnvironmentVariable(envName)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(Path.Combine(WindowsUsersDir, value, ".codex", "sessions"));
            }
        }

        if (Directory.Exists(WindowsUsersDir))
        {
            foreach (var userDir in SafeEnumerateDirectories(WindowsUsersDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(userDir, ".codex", "sessions"));
            }
        }

        return UniquePaths(candidates.Select(CanonicalizePath));
    }

    private IEnumerable<string> DiscoverWindowsSessionsDirsFromWsl()
    {
        return DiscoverWindowsSessionsDirs();
    }

    private IEnumerable<string> DiscoverWslSessionsDirs()
    {
        var candidates = new List<string>();
        foreach (var distroRoot in GetWslDistroRoots())
        {
            foreach (var envName in new[] { "USERNAME", "WIN_USERNAME" })
            {
                var value = Environment.GetEnvironmentVariable(envName)?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidates.Add(Path.Combine(distroRoot, "home", value, ".codex", "sessions"));
                }
            }

            var homeRoot = Path.Combine(distroRoot, "home");
            if (!Directory.Exists(homeRoot))
            {
                continue;
            }

            foreach (var userDir in SafeEnumerateDirectories(homeRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(userDir, ".codex", "sessions"));
            }
        }

        return UniquePaths(candidates.Select(CanonicalizePath));
    }

    private IReadOnlyList<string> GetWslDistroRoots()
    {
        if (_wslDistroRoots is not null)
        {
            return _wslDistroRoots;
        }

        if (!OperatingSystem.IsWindows())
        {
            _wslDistroRoots = Array.Empty<string>();
            return _wslDistroRoots;
        }

        var roots = new List<string>();
        foreach (var distroName in DiscoverWslDistrosFromCommand())
        {
            roots.Add(Path.Combine(WslNetworkRoots[0], distroName));
        }

        if (roots.Count == 0)
        {
            foreach (var networkRoot in WslNetworkRoots)
            {
                foreach (var distroDir in SafeEnumerateDirectories(networkRoot))
                {
                    roots.Add(Path.GetFullPath(distroDir));
                }
            }
        }

        _wslDistroRoots = UniquePaths(roots);
        return _wslDistroRoots;
    }

    private static IEnumerable<string> DiscoverWslDistrosFromCommand()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "-l -q",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Unicode,
                    StandardErrorEncoding = Encoding.Unicode,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore process cleanup errors.
                }

                return Array.Empty<string>();
            }

            if (process.ExitCode != 0)
            {
                return Array.Empty<string>();
            }

            return output
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private string NormalizeSessionsDir(string rawPath)
    {
        foreach (var candidate in ExpandPathCandidates(rawPath))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return CanonicalizePath(rawPath);
    }

    private string CanonicalizePath(string rawPath)
    {
        var candidates = ExpandPathCandidates(rawPath).ToArray();
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        if (candidates.Length > 0)
        {
            return Path.GetFullPath(candidates[0]);
        }

        return Path.GetFullPath(rawPath);
    }

    private IEnumerable<string> ExpandPathCandidates(string rawPath)
    {
        var candidate = Environment.ExpandEnvironmentVariables(rawPath).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Array.Empty<string>();
        }

        if (candidate.StartsWith('~'))
        {
            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                candidate[1..].TrimStart('/', '\\'));
        }

        if (OperatingSystem.IsWindows())
        {
            if (IsWindowsPath(candidate) || IsUncPath(candidate))
            {
                return [Path.GetFullPath(candidate)];
            }

            var fromWslMount = WslMountPathToWindows(candidate);
            if (fromWslMount is not null)
            {
                return [Path.GetFullPath(fromWslMount)];
            }

            var distroCandidates = LinuxPathToWindowsCandidates(candidate).ToArray();
            if (distroCandidates.Length > 0)
            {
                return distroCandidates;
            }

            return [Path.GetFullPath(candidate)];
        }

        var converted = WindowsPathToWsl(candidate);
        if (converted is not null && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            candidate = converted;
        }

        return [Path.GetFullPath(candidate)];
    }

    private IEnumerable<string> LinuxPathToWindowsCandidates(string rawPath)
    {
        if (!OperatingSystem.IsWindows() || !rawPath.StartsWith('/'))
        {
            return Array.Empty<string>();
        }

        var relative = rawPath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return Array.Empty<string>();
        }

        var candidates = GetWslDistroRoots()
            .Select(root => Path.GetFullPath(Path.Combine(root, relative)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Array.Empty<string>();
        }

        var existing = candidates.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        return existing.Length > 0 ? existing : candidates;
    }

    private static bool IsWindowsPath(string rawPath)
    {
        return WindowsPathRegex().IsMatch(rawPath);
    }

    private static bool IsUncPath(string rawPath)
    {
        return rawPath.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string? WslMountPathToWindows(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');
        var match = WslMountPathRegex().Match(normalized);
        if (!match.Success)
        {
            return null;
        }

        var drive = match.Groups[1].Value.ToUpperInvariant();
        var rest = match.Groups[2].Success
            ? match.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar)
            : string.Empty;
        return string.IsNullOrEmpty(rest)
            ? $"{drive}:{Path.DirectorySeparatorChar}"
            : $"{drive}:{Path.DirectorySeparatorChar}{rest}";
    }

    private static string? WindowsPathToWsl(string rawPath)
    {
        var match = WindowsPathRegex().Match(rawPath);
        if (!match.Success)
        {
            return null;
        }

        var drive = match.Groups[1].Value.ToLowerInvariant();
        var rest = match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
        return $"/mnt/{drive}/{rest}";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern, EnumerationOptions options)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern, options).ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryParseJson(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    private static SessionSignature GetSignature(FileInfo fileInfo)
    {
        return new SessionSignature(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
    }

    private static string BuildSessionVersion(FileInfo fileInfo)
    {
        var signature = GetSignature(fileInfo);
        return $"{signature.LastWriteTicks}:{signature.Size}";
    }

    private static bool MatchesTerms(string searchText, IReadOnlyList<string> terms, string mode)
    {
        return mode == "or"
            ? terms.Any(term => searchText.Contains(term, StringComparison.Ordinal))
            : terms.All(term => searchText.Contains(term, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ParseSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var text = query.Trim();
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (inQuotes)
        {
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string NormalizeSearchText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : WhitespaceRegex().Replace(text, " ").Trim().ToLowerInvariant();
    }

    private static string ClassifySource(string rawSource, string originator)
    {
        var source = rawSource.Trim().ToLowerInvariant();
        if (source is "cli" or "vscode")
        {
            return source;
        }

        var origin = originator.Trim().ToLowerInvariant();
        if (source.Contains("vscode", StringComparison.Ordinal) || origin.Contains("vscode", StringComparison.Ordinal))
        {
            return "vscode";
        }

        if (source.Contains("cli", StringComparison.Ordinal) || origin.Contains("cli", StringComparison.Ordinal))
        {
            return "cli";
        }

        return "cli";
    }

    private static bool IsSubagentSource(string rawSource, string originator)
    {
        var source = rawSource.Trim().ToLowerInvariant();
        var origin = originator.Trim().ToLowerInvariant();
        if (source is "exec" or "codex_exec")
        {
            return true;
        }

        if (origin is "exec" or "codex_exec")
        {
            return true;
        }

        return source.Contains("exec", StringComparison.Ordinal)
            || origin.Contains("exec", StringComparison.Ordinal);
    }

    private static string ClassifyUserMessage(string text)
    {
        var lower = text.ToLowerInvariant();
        return ContextMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal))
            ? "user_context"
            : "user";
    }

    private static string[] DetectUserMessageSystemLabels(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("<turn_aborted>", StringComparison.Ordinal)
            && lower.Contains("</turn_aborted>", StringComparison.Ordinal)
            ? ["TURN_ABORTED"]
            : Array.Empty<string>();
    }

    private static string ExtractTextFromContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.Array => string.Join('\n', GetContentTextItems(payload)),
            JsonValueKind.String => content.GetString() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static IEnumerable<string> GetContentTextItems(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    items.Add(text);
                }
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    items.Add(text);
                }
            }
        }

        return items;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static string GetValueText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => property.GetRawText(),
        };
    }

    private static string CollapseNewlines(string text, int maxLength)
    {
        var collapsed = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^([A-Za-z]):[\\/](.*)$")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"^/mnt/([A-Za-z])(?:/(.*))?$")]
    private static partial Regex WslMountPathRegex();

    private sealed class SessionCacheEntry
    {
        public SessionSignature Signature { get; init; }

        public IndexRecord? IndexRecord { get; init; }

        public EventsData? EventsData { get; init; }

        public long PricingVersion { get; init; }

        public long ViewerSettingsVersion { get; init; }

        public int MaxEvents { get; init; }

        private long _lastAccessedTicks = Environment.TickCount64;

        public long LastAccessedTicks
        {
            get => Volatile.Read(ref _lastAccessedTicks);
            set => Volatile.Write(ref _lastAccessedTicks, value);
        }
    }

    private sealed class SessionFilesCacheEntry
    {
        public string RootsKey { get; init; } = string.Empty;

        public DateTime BuiltAtUtc { get; init; }

        public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    }

    private sealed record IndexRecord(SessionSummaryDto Summary, string SearchText);

    private sealed record EventsData(
        IReadOnlyList<SessionEventDto> Events,
        int RawLineCount,
        TokenUsageSummaryDto? Usage);

    private readonly record struct SessionSignature(long LastWriteTicks, long Size);

    private readonly record struct TokenUsageSnapshot(
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        long TotalTokens)
    {
        public bool IsEmpty =>
            InputTokens == 0
            && CachedInputTokens == 0
            && OutputTokens == 0
            && ReasoningOutputTokens == 0
            && TotalTokens == 0;
    }

    private sealed record CostSummaryPeriodDefinition(
        string Key,
        DateTime StartLocal,
        DateTime EndLocal);

    private sealed record CostSummaryGroupDefinition(
        string Key,
        IReadOnlyList<CostSummaryPeriodDefinition> Periods);

    private sealed record CostSummaryCacheEntry(
        DateTimeOffset BuiltAtUtc,
        CostSummaryResponse Response);

    private sealed class CostSummaryGroupAccumulator
    {
        private readonly CostSummaryGroupDefinition _definition;
        private readonly CostSummaryBucketAccumulator[] _sessions;
        private readonly CostSummaryBucketAccumulator[] _tokenUsageEvents;

        public CostSummaryGroupAccumulator(CostSummaryGroupDefinition definition)
        {
            _definition = definition;
            _sessions = definition.Periods.Select(_ => new CostSummaryBucketAccumulator()).ToArray();
            _tokenUsageEvents = definition.Periods.Select(_ => new CostSummaryBucketAccumulator()).ToArray();
        }

        public void AddSessionUsage(DateTime localTimestamp, TokenUsageSummaryDto usage)
        {
            if (TryGetPeriodIndex(localTimestamp, out var index))
            {
                _sessions[index].Add(usage);
            }
        }

        public void AddTokenUsageEvent(DateTime localTimestamp, SessionEventDto usageEvent)
        {
            if (TryGetPeriodIndex(localTimestamp, out var index))
            {
                _tokenUsageEvents[index].Add(usageEvent);
            }
        }

        public CostSummaryGroupDto ToDto()
        {
            return new CostSummaryGroupDto
            {
                Key = _definition.Key,
                Sessions = _definition.Periods
                    .Select((period, index) => _sessions[index].ToDto(period.Key))
                    .ToArray(),
                TokenUsageEvents = _definition.Periods
                    .Select((period, index) => _tokenUsageEvents[index].ToDto(period.Key))
                    .ToArray(),
            };
        }

        private bool TryGetPeriodIndex(DateTime localTimestamp, out int index)
        {
            for (var i = 0; i < _definition.Periods.Count; i++)
            {
                var period = _definition.Periods[i];
                if (localTimestamp >= period.StartLocal && localTimestamp < period.EndLocal)
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }
    }

    private sealed class CostSummaryBucketAccumulator
    {
        private int _itemCount;
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _outputTokens;
        private long _reasoningOutputTokens;
        private long _totalTokens;
        private decimal _costUsd;
        private bool _hasUnknownPricing;

        public void Add(TokenUsageSummaryDto usage)
        {
            _itemCount++;
            _inputTokens += usage.InputTokens;
            _cachedInputTokens += usage.CachedInputTokens;
            _outputTokens += usage.OutputTokens;
            _reasoningOutputTokens += usage.ReasoningOutputTokens;
            _totalTokens += usage.TotalTokens;

            if (usage.CostUsd.HasValue)
            {
                _costUsd += usage.CostUsd.Value;
            }
            else
            {
                _hasUnknownPricing = true;
            }
        }

        public void Add(SessionEventDto usageEvent)
        {
            _itemCount++;
            _inputTokens += usageEvent.InputTokens;
            _cachedInputTokens += usageEvent.CachedInputTokens;
            _outputTokens += usageEvent.OutputTokens;
            _reasoningOutputTokens += usageEvent.ReasoningOutputTokens;
            _totalTokens += usageEvent.TotalTokens;

            if (usageEvent.CostUsd.HasValue)
            {
                _costUsd += usageEvent.CostUsd.Value;
            }
            else
            {
                _hasUnknownPricing = true;
            }
        }

        public CostSummaryPeriodDto ToDto(string key)
        {
            return new CostSummaryPeriodDto
            {
                Key = key,
                ItemCount = _itemCount,
                InputTokens = _inputTokens,
                CachedInputTokens = _cachedInputTokens,
                OutputTokens = _outputTokens,
                ReasoningOutputTokens = _reasoningOutputTokens,
                TotalTokens = _totalTokens,
                CostUsd = _hasUnknownPricing ? null : _costUsd,
            };
        }
    }

    private sealed class TokenUsageAccumulator
    {
        private readonly HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _outputTokens;
        private long _reasoningOutputTokens;
        private long _totalTokens;
        private decimal _costUsd;
        private bool _hasUnknownPricing;

        public bool HasUsage =>
            _inputTokens > 0
            || _cachedInputTokens > 0
            || _outputTokens > 0
            || _reasoningOutputTokens > 0
            || _totalTokens > 0;

        public void Add(SessionEventDto @event)
        {
            _inputTokens += @event.InputTokens;
            _cachedInputTokens += @event.CachedInputTokens;
            _outputTokens += @event.OutputTokens;
            _reasoningOutputTokens += @event.ReasoningOutputTokens;
            _totalTokens += @event.TotalTokens;

            if (!string.IsNullOrWhiteSpace(@event.Model))
            {
                _models.Add(@event.Model);
            }

            if (@event.CostUsd.HasValue)
            {
                _costUsd += @event.CostUsd.Value;
            }
            else
            {
                _hasUnknownPricing = true;
            }
        }

        public TokenUsageSummaryDto ToDto()
        {
            return new TokenUsageSummaryDto
            {
                Models = _models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(),
                InputTokens = _inputTokens,
                CachedInputTokens = _cachedInputTokens,
                OutputTokens = _outputTokens,
                ReasoningOutputTokens = _reasoningOutputTokens,
                TotalTokens = _totalTokens,
                CostUsd = _hasUnknownPricing ? null : _costUsd,
            };
        }
    }
}
