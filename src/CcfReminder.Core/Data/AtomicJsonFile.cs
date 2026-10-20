using System.Text.Json;

namespace CcfReminder.Core.Data;

/// <summary>
/// 小 JSON 文件的原子读写：写入＝临时文件＋改名覆盖；读取失败/损坏一律返回 null，
/// 由调用方回退到默认值（设置、已提醒状态都按此规则安全降级）。
/// </summary>
internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
