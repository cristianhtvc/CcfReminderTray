using CcfReminder.Core.Overrides;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>全部落盘操作都在每次测试随机新建的 %TEMP% 子目录内进行，结束后删除。</summary>
public class OverridesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "CcfReminderTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public OverridesStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "overrides.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DeadlineOverride Sample(string nodeKey = "icml|icml26|PaperSubmission|MainRegular") =>
        new()
        {
            NodeKey = nodeKey,
            ConferenceId = "icml",
            EditionId = "icml26",
            NodeType = DeadlineNodeType.PaperSubmission,
            CorrectedUtc = new DateTimeOffset(2026, 2, 5, 3, 59, 0, TimeSpan.Zero),
            InputLocalTime = "2026-02-05 11:59",
            InputTimezone = "UTC+8",
            UpstreamUtcAtCorrection = new DateTimeOffset(2026, 1, 22, 3, 59, 0, TimeSpan.Zero),
            UpstreamRawTimeAtCorrection = "2026-01-22 19:59",
            UpstreamRawTimezoneAtCorrection = "PST",
            Reason = "官网延期",
            Source = "https://example.org/cfp",
            CorrectedAtUtc = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
        };

    [Fact]
    public void Load_MissingFile_ReturnsMissing()
    {
        var store = new OverridesStore(_path);
        OverridesLoadResult result = store.Load();
        Assert.Equal(OverridesLoadStatus.Missing, result.Status);
        Assert.Empty(result.Overrides);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = new OverridesStore(_path);
        store.Save(new[] { Sample() });

        OverridesLoadResult result = store.Load();

        Assert.Equal(OverridesLoadStatus.Loaded, result.Status);
        DeadlineOverride loaded = Assert.Single(result.Overrides);
        Assert.Equal(Sample(), loaded);
    }

    [Fact]
    public void Save_WritesVersionEnvelope_AndEnumAsString()
    {
        var store = new OverridesStore(_path);
        store.Save(new[] { Sample() });

        string json = File.ReadAllText(_path);
        Assert.Contains("\"Version\": 1", json);
        Assert.Contains("\"PaperSubmission\"", json);
    }

    [Fact]
    public void Load_CorruptedJson_ReportsCorrupted_AndLeavesFileUntouched()
    {
        File.WriteAllText(_path, "{ 这不是合法 JSON …");
        string before = File.ReadAllText(_path);

        var store = new OverridesStore(_path);
        OverridesLoadResult result = store.Load();

        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Overrides); // 安全回退视图，绝不伪装成“空表正常”
        Assert.Equal(before, File.ReadAllText(_path)); // 损坏文件原样保留
    }

    [Fact]
    public void Load_UnsupportedVersion_ReportsCorrupted()
    {
        File.WriteAllText(_path, """{"Version": 99, "Items": []}""");
        var store = new OverridesStore(_path);
        OverridesLoadResult result = store.Load();
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("99", result.Error);
    }

    [Fact]
    public void Load_ItemMissingRequiredField_ReportsCorrupted()
    {
        File.WriteAllText(_path,
            """{"Version": 1, "Items": [{"NodeKey": "k", "ConferenceId": "icml", "Reason": "", "Source": ""}]}""");
        var store = new OverridesStore(_path);
        Assert.Equal(OverridesLoadStatus.Corrupted, store.Load().Status);
    }

    [Fact]
    public void Save_WhenDiskFails_Throws_AndKeepsOldFile()
    {
        // 先写入一份有效文件，再让保存失败（目录被同名文件占用）
        var store = new OverridesStore(_path);
        store.Save(new[] { Sample() });
        string before = File.ReadAllText(_path);

        string blockingDir = Path.Combine(_dir, "blocked");
        File.WriteAllText(blockingDir, "占位文件：同名目录无法创建");
        var failingStore = new OverridesStore(Path.Combine(blockingDir, "overrides.json"));

        Assert.ThrowsAny<IOException>(() => failingStore.Save(new[] { Sample("k2") }));
        Assert.Equal(before, File.ReadAllText(_path)); // 原文件未被波及
    }

    [Fact]
    public void Save_LeavesNoTempFilesBehind()
    {
        var store = new OverridesStore(_path);
        store.Save(new[] { Sample() });
        Assert.Equal(new[] { _path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void BackupCorruptFileCopy_CopiesAndKeepsOriginal()
    {
        File.WriteAllText(_path, "损坏内容");
        var store = new OverridesStore(_path);
        string backup = store.BackupCorruptFileCopy(new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc));

        // 复制而非移动：原件仍在原路径（空表提交失败时重启仍判损坏的关键）
        Assert.True(File.Exists(_path));
        Assert.Equal("损坏内容", File.ReadAllText(_path));
        Assert.True(File.Exists(backup));
        Assert.Contains(".corrupt-20261014093000.bak", backup);
        Assert.Equal("损坏内容", File.ReadAllText(backup));
    }

    [Fact]
    public void BackupCorruptFileCopy_SameTimestamp_UniqueNames()
    {
        File.WriteAllText(_path, "损坏内容");
        var store = new OverridesStore(_path);
        var when = new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc);
        string first = store.BackupCorruptFileCopy(when);
        string second = store.BackupCorruptFileCopy(when);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    // ---- 严格 Load：有效空表与损坏必须明确区分（问题 1 回归）----

    /// <summary>一条全字段合法的条目（键与字段一致、零偏移 UTC）。</summary>
    private const string ValidItemJson = """
        {"NodeKey":"icml|icml26|PaperSubmission|MainRegular","ConferenceId":"icml","EditionId":"icml26",
         "NodeType":"PaperSubmission","CorrectedUtc":"2026-02-05T03:59:00+00:00",
         "InputLocalTime":"2026-02-05 11:59","InputTimezone":"UTC+8",
         "UpstreamUtcAtCorrection":"2026-01-22T03:59:00+00:00",
         "UpstreamRawTimeAtCorrection":"2026-01-22 19:59","UpstreamRawTimezoneAtCorrection":"PST",
         "Reason":"官网延期","Source":"https://example.org","CorrectedAtUtc":"2026-10-01T08:30:00+00:00"}
        """;

    private OverridesLoadResult LoadJson(string json)
    {
        File.WriteAllText(_path, json);
        return new OverridesStore(_path).Load();
    }

    [Fact]
    public void Load_EmptyItems_IsValidEmptyTable_NotCorrupted()
    {
        OverridesLoadResult result = LoadJson("""{"Version":1,"Items":[]}""");
        Assert.Equal(OverridesLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Overrides);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Load_ValidItem_Loaded()
    {
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{ValidItemJson}]}}");
        Assert.Equal(OverridesLoadStatus.Loaded, result.Status);
        Assert.Single(result.Overrides);
    }

    [Theory]
    [InlineData("""{"Version":1}""")]                 // 缺 Items：不是有效空表
    [InlineData("""{"Version":1,"Items":null}""")]    // Items:null 同上
    [InlineData("""{"Version":1,"Items":[null]}""")]  // 条目为 null
    public void Load_MissingOrNullItems_Corrupted_NotEmptyTable(string json)
    {
        OverridesLoadResult result = LoadJson(json);
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Empty(result.Overrides); // 安全回退视图，不是“载入空表”
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_DuplicateNodeKeys_Corrupted_NeverLastWins()
    {
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{ValidItemJson},{ValidItemJson}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("重复", result.Error);
        Assert.Empty(result.Overrides);
    }

    [Fact]
    public void Load_UnknownNodeTypeNumber_Corrupted()
    {
        string json = $$"""{"Version":1,"Items":[{{ValidItemJson.Replace("\"PaperSubmission\"", "999")}}]}""";
        OverridesLoadResult result = LoadJson(json);
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("999", result.Error);
    }

    [Fact]
    public void Load_IneligibleNodeType_Corrupted()
    {
        // Rebuttal 不开放修正：即便键也改成一致，记录本身不合法
        string item = ValidItemJson
            .Replace("\"PaperSubmission\"", "\"Rebuttal\"")
            .Replace("PaperSubmission|MainRegular", "Rebuttal|MainRegular");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("不开放修正", result.Error);
    }

    [Fact]
    public void Load_KeyFieldMismatch_Corrupted()
    {
        // 键写 AbstractRegistration，字段写 PaperSubmission：不一致
        string item = ValidItemJson.Replace(
            "icml|icml26|PaperSubmission|MainRegular", "icml|icml26|AbstractRegistration|MainRegular");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("不一致", result.Error);
    }

    [Fact]
    public void Load_NonZeroUtcOffset_Corrupted()
    {
        string item = ValidItemJson.Replace("2026-02-05T03:59:00+00:00", "2026-02-05T11:59:00+08:00");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("偏移", result.Error);
    }

    [Fact]
    public void Load_InvalidTimeString_Corrupted_NoUnhandledException()
    {
        string item = ValidItemJson.Replace("\"2026-02-05T03:59:00+00:00\"", "\"not-a-time\"");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.NotNull(result.Error);
    }

    // ---- CorrectedUtc/CorrectedAtUtc 缺失/null/default 绝不静默放行（问题 1 回归）----

    /// <summary>从合法条目 JSON 中移除一个字段（连同相邻逗号，保证剩余仍是合法 JSON）。</summary>
    private static string RemoveField(string itemJson, string fieldName)
    {
        int keyIndex = itemJson.IndexOf($"\"{fieldName}\":", StringComparison.Ordinal);
        Assert.True(keyIndex >= 0, $"字段 {fieldName} 不在样例 JSON 中");
        int valueEnd = itemJson.IndexOfAny(new[] { ',', '}' }, keyIndex);
        if (itemJson[valueEnd] == ',')
        {
            return itemJson.Remove(keyIndex, valueEnd - keyIndex + 1); // 连同后随逗号
        }
        int commaBefore = itemJson.LastIndexOf(',', keyIndex);
        return itemJson.Remove(commaBefore, valueEnd - commaBefore); // 末尾字段：连同前导逗号
    }

    [Theory]
    [InlineData("CorrectedUtc")]
    [InlineData("CorrectedAtUtc")]
    public void Load_MissingUtcField_Corrupted_NotDefaulted(string field)
    {
        string item = RemoveField(ValidItemJson, field);
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains(field, result.Error); // 明确指认缺失字段，不冒充 0001-01-01
        Assert.Empty(result.Overrides);
    }

    [Theory]
    [InlineData("CorrectedUtc")]
    [InlineData("CorrectedAtUtc")]
    public void Load_NullUtcField_Corrupted(string field)
    {
        // 把合法值替换为显式 null
        string value = field == "CorrectedUtc" ? "2026-02-05T03:59:00+00:00" : "2026-10-01T08:30:00+00:00";
        string item = ValidItemJson.Replace($"\"{value}\"", "null");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains(field, result.Error);
    }

    [Theory]
    [InlineData("CorrectedUtc", "2026-02-05T03:59:00+00:00")]
    [InlineData("CorrectedAtUtc", "2026-10-01T08:30:00+00:00")]
    public void Load_DefaultUtcValue_Corrupted(string field, string legitValue)
    {
        string item = ValidItemJson.Replace(legitValue, "0001-01-01T00:00:00+00:00");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains(field, result.Error);
    }

    [Fact]
    public void Load_NullUpstreamUtcAtCorrection_Allowed()
    {
        // UpstreamUtcAtCorrection 本就允许 null（上游当时 TBD/无法换算）
        string item = ValidItemJson.Replace("\"2026-01-22T03:59:00+00:00\"", "null");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Loaded, result.Status);
        Assert.Single(result.Overrides);
        Assert.Null(result.Overrides[0].UpstreamUtcAtCorrection);
    }

    [Fact]
    public void Load_OldButValidYear_Allowed()
    {
        // 不设任意年份下限：合法的历史年份照常载入
        string item = ValidItemJson.Replace("2026-02-05T03:59:00+00:00", "1999-01-15T03:59:00+00:00");
        OverridesLoadResult result = LoadJson($"{{\"Version\":1,\"Items\":[{item}]}}");
        Assert.Equal(OverridesLoadStatus.Loaded, result.Status);
        Assert.Equal(1999, result.Overrides[0].CorrectedUtc.Year);
    }

    [Fact]
    public void Load_CorruptedInput_PreservesOriginalBytes()
    {
        const string json = """{"Version":1,"Items":null}""";
        File.WriteAllText(_path, json);
        OverridesLoadResult result = new OverridesStore(_path).Load();
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Equal(json, File.ReadAllText(_path)); // 原文件字节不变
    }

    [Fact]
    public void Load_PendingMarkerWithoutMainFile_ReportsCorrupted_NotNewUser()
    {
        // 上次重新初始化中断：备份成功、空表未写入、原路径缺失 → 仍是损坏待处理
        File.WriteAllText(_path + ".reinit-pending", "你的修正已备份到：C:\\x\\overrides.json.corrupt-20261014.bak");
        OverridesLoadResult result = new OverridesStore(_path).Load();
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
        Assert.Contains("中途失败", result.Error);
        Assert.Contains("corrupt-20261014.bak", result.Error);
    }

    [Fact]
    public void Load_PendingMarkerUnreadable_StillCorrupted()
    {
        File.WriteAllText(_path + ".reinit-pending", "x");
        using var lockStream = new FileStream(_path + ".reinit-pending", FileMode.Open, FileAccess.Read, FileShare.None);
        OverridesLoadResult result = new OverridesStore(_path).Load();
        Assert.Equal(OverridesLoadStatus.Corrupted, result.Status);
    }
}
