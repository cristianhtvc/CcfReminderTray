using System.Threading;
using CcfReminder.Core.Ccfddl;

namespace CcfReminder.Core.Data;

/// <summary>一个会议的完整数据集（含上游等级标签，仅供详情展示）。</summary>
public sealed record ConferenceDataset(
    ConferenceInfo Identity,
    string? RankCcfUpstream,
    IReadOnlyList<DeadlineNode> Nodes);

/// <summary>一次成功获取/载入的数据快照（不可变）。</summary>
public sealed record ConferenceDataSnapshot(
    DateTimeOffset FetchedAtUtc,
    string SourceName,
    IReadOnlyList<ConferenceDataset> Conferences);

public enum RefreshStatus
{
    Success,
    Failed,
    AlreadyRunning,
}

public sealed record RefreshOutcome(
    RefreshStatus Status,
    string? Error,
    DateTimeOffset? CacheFetchedAtUtc);

/// <summary>
/// 数据接入服务：下载 allconf.json → 结构校验（含六会议身份核对）→ 原子提交本地缓存。
/// 下载、校验、写临时文件、提交任一步失败都保留内存旧快照并返回明确的 Failed；
/// 除调用方取消外，不因磁盘/网络等预期异常向外抛错。
/// </summary>
public sealed class ConferenceDataService
{
    public static readonly Uri SourceUri = new("https://ccfddl.com/conference/allconf.json");

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SnapshotCacheStore _store;
    private int _refreshing;

    public ConferenceDataService(HttpClient http, string cacheDirectory, Func<DateTimeOffset> utcNow)
        : this(http, new SnapshotCacheStore(cacheDirectory), utcNow)
    {
    }

    public ConferenceDataService(HttpClient http, SnapshotCacheStore store, Func<DateTimeOffset> utcNow)
    {
        _http = http;
        _store = store;
        _utcNow = utcNow;
    }

    /// <summary>当前有效快照；从未成功获取时为 null。</summary>
    public ConferenceDataSnapshot? Current { get; private set; }

    /// <summary>最近一次刷新/载入的失败原因；成功时为 null。</summary>
    public string? LastError { get; private set; }

    public bool IsRefreshing => _refreshing == 1;

    public string CacheFilePath => _store.CurrentCachePath;

    /// <summary>
    /// 启动时从本地缓存载入（不联网）。优先 v2 信封、回退旧格式双文件；
    /// “选中哪份缓存”取决于完整的数据结构与六会议身份校验，而不只取决于信封可反序列化：
    /// v2 的 Payload 无法通过校验时继续尝试旧格式，两份都无效才按“无有效缓存”处理（不删除文件）。
    /// </summary>
    public void LoadCache()
    {
        if (TryLoadCandidate(_store.TryLoadRawV2) ||
            TryLoadCandidate(_store.TryLoadRawLegacy))
        {
            LastError = null;
        }

        bool TryLoadCandidate(LoadRawCandidate loadRaw)
        {
            string? raw;
            DateTimeOffset? fetchedAt;
            try
            {
                raw = loadRaw(out fetchedAt);
            }
            catch
            {
                return false; // 存储层异常按该候选不可用处理；绝不因缓存异常导致启动失败
            }

            if (raw is null || fetchedAt is null)
            {
                return false;
            }
            if (!TryBuildFromJson(raw, fetchedAt.Value, out ConferenceDataSnapshot? snap, out _))
            {
                return false; // 内容不完整/身份不符：这份不算有效缓存，继续尝试下一份
            }
            Current = snap;
            return true;
        }
    }

    private delegate string? LoadRawCandidate(out DateTimeOffset? fetchedAtUtc);

    /// <summary>后台刷新。并发调用只有一次真正执行，其余立即返回 AlreadyRunning。</summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return new RefreshOutcome(RefreshStatus.AlreadyRunning, null, Current?.FetchedAtUtc);
        }

        try
        {
            string json;
            try
            {
                json = await _http.GetStringAsync(SourceUri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail("网络请求超时");
            }
            catch (HttpRequestException ex)
            {
                return Fail($"网络错误: {ex.Message}");
            }

            DateTimeOffset fetchedAt = _utcNow();
            if (!TryBuildFromJson(json, fetchedAt, out ConferenceDataSnapshot? snap, out string? err))
            {
                return Fail($"数据结构校验未通过: {err}");
            }

            try
            {
                _store.Commit(json, fetchedAt);
            }
            catch (IOException ex)
            {
                return Fail($"缓存写入失败: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail($"缓存写入失败: {ex.Message}");
            }

            Current = snap;
            LastError = null;
            return new RefreshOutcome(RefreshStatus.Success, null, fetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消（如应用退出）正常传播
        }
        catch (Exception ex)
        {
            // 任何未预期错误都转为明确失败，不让后台任务悄悄死掉
            return Fail($"未预期错误: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }

        RefreshOutcome Fail(string error)
        {
            LastError = error;
            return new RefreshOutcome(RefreshStatus.Failed, error, Current?.FetchedAtUtc);
        }
    }

    /// <summary>解析 + 六会议身份核对；任一步失败都不算有效数据。</summary>
    private static bool TryBuildFromJson(
        string json,
        DateTimeOffset fetchedAt,
        out ConferenceDataSnapshot? snapshot,
        out string? error)
    {
        if (!CcfddlJsonParser.TryParse(json, out IReadOnlyList<ParsedConference>? parsed, out error))
        {
            snapshot = null;
            return false;
        }
        return CcfddlAdapter.TryBuildSnapshot(parsed, fetchedAt, out snapshot, out error);
    }
}
