using System.Text.Json;

namespace CcfReminder.Core.Data;

/// <summary>
/// 快照缓存的文件读写。
/// 新格式为单文件信封 snapshot-v2.json（获取时间与数据同进同出）：
/// 提交＝写临时文件后一次原子改名，重启后只能读到完整新快照或上一份有效快照。
/// 兼容读取旧格式 allconf.json＋allconf.meta.json（只读，不再写入）。
/// </summary>
public class SnapshotCacheStore
{
    public const string V2FileName = "snapshot-v2.json";
    public const string LegacyDataFileName = "allconf.json";
    public const string LegacyMetaFileName = "allconf.meta.json";

    private readonly string _dir;

    public SnapshotCacheStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>当前生效缓存（v2）的完整路径。</summary>
    public string CurrentCachePath => Path.Combine(_dir, V2FileName);

    public string LegacyDataPath => Path.Combine(_dir, LegacyDataFileName);
    public string LegacyMetaPath => Path.Combine(_dir, LegacyMetaFileName);

    private sealed record CacheEnvelope(DateTimeOffset FetchedAtUtc, string Payload);

    /// <summary>
    /// 读取 v2 信封的原始数据与配套获取时间（仅保证信封可反序列化，不校验 Payload 内容；
    /// 内容是否算有效缓存由调用方做完整结构与身份校验后决定）。
    /// </summary>
    public virtual string? TryLoadRawV2(out DateTimeOffset? fetchedAtUtc)
    {
        fetchedAtUtc = null;
        try
        {
            if (!File.Exists(CurrentCachePath))
            {
                return null;
            }
            CacheEnvelope? envelope =
                JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(CurrentCachePath));
            // 缺失 FetchedAtUtc（反序列化后为默认值）或时间为默认值：时间与数据不配套，候选无效
            if (envelope is null ||
                string.IsNullOrEmpty(envelope.Payload) ||
                envelope.FetchedAtUtc == default)
            {
                return null;
            }
            fetchedAtUtc = envelope.FetchedAtUtc;
            return envelope.Payload;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>读取旧格式 allconf.json＋allconf.meta.json（兼容已有缓存；同样只做文件层读取）。</summary>
    public virtual string? TryLoadRawLegacy(out DateTimeOffset? fetchedAtUtc)
    {
        fetchedAtUtc = null;
        try
        {
            if (!File.Exists(LegacyDataPath) || !File.Exists(LegacyMetaPath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyMetaPath));
            if (!doc.RootElement.TryGetProperty("fetchedAtUtc", out JsonElement el) ||
                el.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(el.GetString(), out DateTimeOffset fetchedAt))
            {
                return null;
            }
            fetchedAtUtc = fetchedAt;
            return File.ReadAllText(LegacyDataPath);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>原子提交：先写临时文件，再一次改名覆盖。任何一步失败都不影响旧快照。</summary>
    public virtual void Commit(string payload, DateTimeOffset fetchedAtUtc)
    {
        string tmp = CurrentCachePath + ".tmp";
        File.WriteAllText(tmp,
            JsonSerializer.Serialize(new CacheEnvelope(fetchedAtUtc, payload)));
        File.Move(tmp, CurrentCachePath, overwrite: true);
    }
}
