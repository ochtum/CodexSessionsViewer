using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSessionsViewer.Models;

namespace CodexSessionsViewer.Services;

public sealed partial class ViewerService
{
    private const int MaxList = 300;
    private const int MaxEvents = 2000;
    private const int SearchTextLimit = 50_000;
    private const int MaxCacheEntries = 500;
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
    private readonly ConcurrentDictionary<string, SessionCacheEntry> _cache = new(PathComparer);
    private IReadOnlyList<string>? _sessionRoots;
    private IReadOnlyList<string>? _wslDistroRoots;

    public ViewerService(LabelStore labelStore)
    {
        _labelStore = labelStore;
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

    public async Task<SessionListResponse> GetSessionsAsync(
        string? query,
        string? mode,
        string? sort,
        int? sessionLabelId,
        int? eventLabelId,
        CancellationToken cancellationToken = default)
    {
        var roots = GetSessionRoots();
        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var normalizedMode = string.Equals(mode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and";
        var normalizedSort = sort is "asc" or "updated" ? sort : "desc";
        var terms = ParseSearchQuery(query)
            .Select(NormalizeSearchText)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        var sessions = new List<SessionSummaryDto>();
        foreach (var path in EnumerateSessionFiles(roots))
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

        return new SessionListResponse
        {
            Root = string.Join(" | ", roots),
            Sessions = ordered.Take(MaxList).ToArray(),
        };
    }

    public async Task<SessionDetailResponse> GetSessionAsync(string? rawPath, bool includeEvents, CancellationToken cancellationToken = default)
    {
        var path = ResolveSessionPath(rawPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("session file not found");
        }

        var snapshot = await _labelStore.GetSnapshotAsync(cancellationToken);
        var indexRecord = GetOrBuildIndexRecord(path);
        var sessionPath = indexRecord.Summary.Path;
        var sessionLabelIds = snapshot.SessionLabels.TryGetValue(sessionPath, out var sIds) ? sIds : Array.Empty<int>();

        if (!includeEvents)
        {
            return new SessionDetailResponse
            {
                Session = WithSessionLabels(
                    indexRecord.Summary,
                    sessionLabelIds,
                    ResolveLabels(sessionLabelIds, snapshot.LabelById)),
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
        if (_cache.TryGetValue(path, out var cached)
            && cached.Signature == signature
            && cached.EventsData is not null)
        {
            cached.LastAccessedTicks = Environment.TickCount64;
            return cached.EventsData;
        }

        var built = BuildEventsData(path);
        var next = new SessionCacheEntry
        {
            Signature = signature,
            IndexRecord = cached is not null && cached.Signature == signature ? cached.IndexRecord : null,
            EventsData = built,
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
            Source = "cli",
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
                            };
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

    private EventsData BuildEventsData(string path)
    {
        var events = new List<SessionEventDto>();
        var rawLineCount = 0;
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

                if (type == "response_item")
                {
                    var responseType = GetString(payload, "type");
                    if (responseType == "message")
                    {
                        var role = GetString(payload, "role");
                        var text = ExtractTextFromContent(payload);
                        if (!string.IsNullOrWhiteSpace(text))
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
                    else if (responseType == "function_call")
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
                    else if (responseType == "function_call_output")
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
                else if (type == "event_msg" && GetString(payload, "type") == "agent_message")
                {
                    events.Add(new SessionEventDto
                    {
                        EventId = $"line-{rawLineCount}",
                        Timestamp = timestamp,
                        Kind = "agent_update",
                        Text = GetValueText(payload, "message"),
                    });
                }
            }

            if (events.Count >= MaxEvents)
            {
                break;
            }
        }

        return new EventsData(events, rawLineCount);
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

    private IEnumerable<string> EnumerateSessionFiles(IEnumerable<string> roots)
    {
        var files = new Dictionary<string, FileInfo>(PathComparer);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var root in roots)
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

        return files.Values
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, PathComparer)
            .Select(file => file.FullName)
            .ToArray();
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

        private long _lastAccessedTicks = Environment.TickCount64;

        public long LastAccessedTicks
        {
            get => Volatile.Read(ref _lastAccessedTicks);
            set => Volatile.Write(ref _lastAccessedTicks, value);
        }
    }

    private sealed record IndexRecord(SessionSummaryDto Summary, string SearchText);

    private sealed record EventsData(IReadOnlyList<SessionEventDto> Events, int RawLineCount);

    private readonly record struct SessionSignature(long LastWriteTicks, long Size);
}
