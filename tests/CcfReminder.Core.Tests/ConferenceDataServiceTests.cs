using System.Net;
using System.Text.Json;
using CcfReminder.Core.Data;
using Xunit;

namespace CcfReminder.Core.Tests;

public class ConferenceDataServiceTests
{
    private static readonly DateTimeOffset Now1 = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now2 = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;
        public int CallCount;

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _respond(cancellationToken);
        }
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), "ccf-reminder-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refresh_Success_WritesCacheAndSnapshot()
    {
        string dir = NewCacheDir();
        var handler = new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())));
        var service = new ConferenceDataService(new HttpClient(handler), dir, () => Now1);

        RefreshOutcome outcome = await service.RefreshAsync();

        Assert.Equal(RefreshStatus.Success, outcome.Status);
        Assert.NotNull(service.Current);
        Assert.Equal(Now1, service.Current!.FetchedAtUtc);
        Assert.True(File.Exists(service.CacheFilePath));
        Assert.Equal(6, service.Current.Conferences.Count);
    }

    [Fact]
    public async Task Refresh_HttpError_KeepsOldCacheAndSnapshot()
    {
        string dir = NewCacheDir();
        var good = new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())));
        var service = new ConferenceDataService(new HttpClient(good), dir, () => Now1);
        await service.RefreshAsync();
        string cachedText = File.ReadAllText(service.CacheFilePath);

        var failing = new ConferenceDataService(
            new HttpClient(new StubHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)))), dir, () => Now2);
        failing.LoadCache(); // 模拟重启后从旧缓存启动
        RefreshOutcome outcome = await failing.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Contains("网络错误", outcome.Error);
        Assert.NotNull(failing.Current);               // 旧快照仍在
        Assert.Equal(Now1, failing.Current!.FetchedAtUtc);
        Assert.Equal(cachedText, File.ReadAllText(service.CacheFilePath)); // 缓存未被清空
    }

    [Fact]
    public async Task Refresh_CorruptedJson_KeepsOldCache()
    {
        string dir = NewCacheDir();
        var service = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await service.RefreshAsync();
        string cachedText = File.ReadAllText(service.CacheFilePath);

        var corrupted = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk("not json{")))), dir, () => Now2);
        corrupted.LoadCache();
        RefreshOutcome outcome = await corrupted.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Equal(Now1, corrupted.Current!.FetchedAtUtc);
        Assert.Equal(cachedText, File.ReadAllText(service.CacheFilePath));
    }

    [Fact]
    public async Task Refresh_IdentityIncomplete_RejectedKeepsOldCache()
    {
        string dir = NewCacheDir();
        var service = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await service.RefreshAsync();
        string cachedText = File.ReadAllText(service.CacheFilePath);

        // 结构合法但缺 DAI 的 JSON：不得替换旧缓存
        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.FiveConferencesMissingDai())))), dir, () => Now2);
        svc.LoadCache();
        RefreshOutcome outcome = await svc.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Contains("DAI", outcome.Error);
        Assert.Equal(cachedText, File.ReadAllText(service.CacheFilePath));
    }

    [Fact]
    public async Task Refresh_Timeout_KeepsOldCache()
    {
        string dir = NewCacheDir();
        var service = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await service.RefreshAsync();

        var slow = new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // 会被 HttpClient.Timeout 取消
            return JsonOk(TestJson.SixConferences());
        });
        var http = new HttpClient(slow) { Timeout = TimeSpan.FromMilliseconds(300) };
        var svc = new ConferenceDataService(http, dir, () => Now2);
        svc.LoadCache();
        RefreshOutcome outcome = await svc.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Contains("超时", outcome.Error);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }

    [Fact]
    public async Task Refresh_Concurrent_OnlyOneRuns()
    {
        string dir = NewCacheDir();
        var slow = new StubHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return JsonOk(TestJson.SixConferences());
        });
        var service = new ConferenceDataService(new HttpClient(slow), dir, () => Now1);

        Task<RefreshOutcome> first = service.RefreshAsync();
        Task<RefreshOutcome> second = service.RefreshAsync();
        RefreshOutcome[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, slow.CallCount);
        Assert.Contains(results, r => r.Status == RefreshStatus.Success);
        Assert.Contains(results, r => r.Status == RefreshStatus.AlreadyRunning);
    }

    [Fact]
    public async Task LoadCache_WorksOffline()
    {
        string dir = NewCacheDir();
        // 先通过一次成功刷新建立缓存
        var writer = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await writer.RefreshAsync();

        // 模拟无网环境重启：handler 被调用就抛异常
        var offline = new StubHandler(_ => throw new HttpRequestException("offline"));
        var svc = new ConferenceDataService(new HttpClient(offline), dir, () => Now2);
        svc.LoadCache();

        Assert.Equal(0, offline.CallCount); // 载入缓存不发网络请求
        Assert.NotNull(svc.Current);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }

    [Fact]
    public void LoadCache_CorruptCache_TreatedAsNoData_FileKept()
    {
        string dir = NewCacheDir();
        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        File.WriteAllText(svc.CacheFilePath, "garbage");

        svc.LoadCache();
        Assert.Null(svc.Current); // 按无数据处理
        Assert.True(File.Exists(svc.CacheFilePath)); // 文件不删除
    }

    [Fact]
    public void LoadCache_LegacyFormat_StillReadable()
    {
        // 兼容升级前的 allconf.json＋allconf.meta.json 缓存
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.NotNull(svc.Current);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }

    [Fact]
    public async Task LoadCache_LegacyUpgraded_OnNextSuccessfulRefresh()
    {
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now2);
        svc.LoadCache();
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc); // 旧格式可读
        await svc.RefreshAsync();

        // “重启”后读到新快照（v2 优先于旧文件）
        var restarted = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now1);
        restarted.LoadCache();
        Assert.Equal(Now2, restarted.Current!.FetchedAtUtc);
    }

    [Fact]
    public void LoadCache_V2Corrupt_FallsBackToLegacy()
    {
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        File.WriteAllText(store.CurrentCachePath, "corrupt-v2");
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }

    /// <summary>写一个信封层合法的 v2 缓存文件（Payload 内容可控，用于注入“内容非法”故障）。</summary>
    private static void WriteV2Envelope(string path, DateTimeOffset fetchedAt, string payload) =>
        File.WriteAllText(path,
            JsonSerializer.Serialize(new { FetchedAtUtc = fetchedAt, Payload = payload }));

    [Fact]
    public void LoadCache_V2PayloadNotJson_FallsBackToLegacyWithOwnTimestamp()
    {
        // v2 信封可反序列化，但 Payload 不是有效 JSON：不得选中 v2，应回退旧版完整快照
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        WriteV2Envelope(store.CurrentCachePath, Now2, "not json{");
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.NotNull(svc.Current); // 不是“暂无数据”
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc); // 旧版的原获取时间，不是 v2 信封上的 Now2
        Assert.Equal(6, svc.Current.Conferences.Count);
    }

    [Fact]
    public void LoadCache_V2PayloadMissingConference_FallsBackToLegacy()
    {
        // v2 信封合法、Payload 是合法 JSON 但缺 DAI：身份校验不通过，同样回退旧版
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        WriteV2Envelope(store.CurrentCachePath, Now2, TestJson.FiveConferencesMissingDai());
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.NotNull(svc.Current);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
        Assert.Equal(6, svc.Current.Conferences.Count);
    }

    [Fact]
    public void LoadCache_V2AndLegacyBothInvalid_TreatedAsNoData()
    {
        // v2 Payload 非法，旧版数据也损坏：两份都不算有效缓存
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        WriteV2Envelope(store.CurrentCachePath, Now2, "not json{");
        File.WriteAllText(store.LegacyDataPath, "garbage");
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.Null(svc.Current); // 准确按“无有效缓存”处理
    }

    [Fact]
    public void LoadCache_V2MissingFetchedAt_FallsBackToLegacy()
    {
        // v2 Payload 完整有效但信封缺 FetchedAtUtc：时间与数据不配套，候选无效，回退旧版
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        File.WriteAllText(store.CurrentCachePath,
            JsonSerializer.Serialize(new { Payload = TestJson.SixConferences() }));
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.NotNull(svc.Current);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc); // 旧版自身获取时间
        Assert.Equal(6, svc.Current.Conferences.Count);
    }

    [Fact]
    public void LoadCache_V2DefaultFetchedAt_FallsBackToLegacy()
    {
        // FetchedAtUtc 为 DateTimeOffset 默认值同样视为无效候选
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        WriteV2Envelope(store.CurrentCachePath, default, TestJson.SixConferences());
        File.WriteAllText(store.LegacyDataPath, TestJson.SixConferences());
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.NotNull(svc.Current);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }

    [Fact]
    public void LoadCache_V2MissingFetchedAt_AndLegacyInvalid_TreatedAsNoData()
    {
        // v2 缺时间、旧版数据损坏：两份都无效 → 无有效缓存
        string dir = NewCacheDir();
        var store = new SnapshotCacheStore(dir);
        File.WriteAllText(store.CurrentCachePath,
            JsonSerializer.Serialize(new { Payload = TestJson.SixConferences() }));
        File.WriteAllText(store.LegacyDataPath, "garbage");
        File.WriteAllText(store.LegacyMetaPath, """{"fetchedAtUtc":"2026-10-13T08:00:00+00:00"}""");

        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        svc.LoadCache();

        Assert.Null(svc.Current);
    }

    /// <summary>提交阶段整体抛盘错的存储（故障注入）。</summary>
    private sealed class FaultingCommitStore : SnapshotCacheStore
    {
        public FaultingCommitStore(string dir) : base(dir) { }
        public override void Commit(string payload, DateTimeOffset fetchedAtUtc) =>
            throw new IOException("injected disk fault");
    }

    /// <summary>模拟“写入中途失败”：留下半截临时文件后抛错。</summary>
    private sealed class MidWriteFaultStore : SnapshotCacheStore
    {
        public MidWriteFaultStore(string dir) : base(dir) { }
        public override void Commit(string payload, DateTimeOffset fetchedAtUtc)
        {
            File.WriteAllText(CurrentCachePath + ".tmp", "partial garbage");
            throw new IOException("injected mid-write fault");
        }
    }

    [Fact]
    public async Task Refresh_CommitFault_FailedKeepsMemorySnapshot_RestartSeesOld()
    {
        string dir = NewCacheDir();
        // 先建立一份有效缓存（Now1）
        var good = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await good.RefreshAsync();

        // 故障注入：下载成功但提交失败
        var store = new FaultingCommitStore(dir);
        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), store, () => Now2);
        svc.LoadCache();
        RefreshOutcome outcome = await svc.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Contains("缓存写入失败", outcome.Error);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc); // 内存旧快照保留

        // “重启”后仍是完整旧快照与旧获取时间
        var restarted = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        restarted.LoadCache();
        Assert.Equal(Now1, restarted.Current!.FetchedAtUtc);
    }

    [Fact]
    public async Task Refresh_MidWriteFault_RestartSeesOldSnapshot_TmpIgnored()
    {
        string dir = NewCacheDir();
        var good = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await good.RefreshAsync();

        var store = new MidWriteFaultStore(dir);
        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), store, () => Now2);
        svc.LoadCache();
        RefreshOutcome outcome = await svc.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.True(File.Exists(store.CurrentCachePath + ".tmp")); // 半截临时文件存在

        var restarted = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => throw new HttpRequestException("offline"))), dir, () => Now2);
        restarted.LoadCache();
        Assert.NotNull(restarted.Current);
        Assert.Equal(Now1, restarted.Current!.FetchedAtUtc); // 旧数据与旧时间仍配套
    }

    [Fact]
    public async Task Refresh_StructuralFormatError_RejectedKeepsOldCache()
    {
        string dir = NewCacheDir();
        var good = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(TestJson.SixConferences())))), dir, () => Now1);
        await good.RefreshAsync();

        // timeline 存在但不是数组：结构错误，不得当作“无节点”写入缓存
        string badTimeline = TestJson.SixConferences(
            iclr: """{"year":2027,"id":"iclr27","link":"https://example.org/c","timeline":{"deadline":"2027-09-25 23:59:59"},"timezone":"AoE"}""");
        var svc = new ConferenceDataService(new HttpClient(
            new StubHandler(_ => Task.FromResult(JsonOk(badTimeline)))), dir, () => Now2);
        svc.LoadCache();
        RefreshOutcome outcome = await svc.RefreshAsync();

        Assert.Equal(RefreshStatus.Failed, outcome.Status);
        Assert.Contains("timeline", outcome.Error);
        Assert.Equal(Now1, svc.Current!.FetchedAtUtc);
    }
}
