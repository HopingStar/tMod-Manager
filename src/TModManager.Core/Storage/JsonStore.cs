using System.Text.Json;
using TModManager.Core.Configuration;
using TModManager.Core.Models;

namespace TModManager.Core.Storage;

/// <summary>
/// data.json 读写（全局门锁）。
///
/// <list type="bullet">
///   <item><b>原子写</b>：先写 <c>data.json.tmp</c>，再用 <c>File.Replace</c> 原子顶上，并把上一份留成
///     <c>data.json.bak</c> —— 崩溃 / 断电 / 被安全软件打断都不会把配置写成半截。</item>
///   <item><b>损坏不再静默</b>：解析失败时把坏文件改名成 <c>data.json.corrupt-&lt;时间戳&gt;</c> 留证，
///     并尝试从 <c>.bak</c> 恢复；结果记在 <see cref="LastLoadWarning"/>，由 /api/status 透出给界面。</item>
/// </list>
/// </summary>
public sealed class JsonStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonStore(AppPaths paths) => _paths = paths;

    /// <summary>
    /// 最近一次读取遇到的异常情况（文件损坏 / 已从备份恢复）。正常时为 null。
    /// 只在第一次出问题时写入，之后不再覆盖，避免后续读把更重要的信息冲掉。
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    private string BackupFile => _paths.DataFile + ".bak";

    public async Task<AppData> ReadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteAsync(AppData data)
    {
        await _lock.WaitAsync();
        try
        {
            await WriteInternalAsync(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>原子「读-改-写」：在门锁内执行 mutate 并落盘，返回 mutate 的结果。</summary>
    public async Task<T> UpdateAsync<T>(Func<AppData, T> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await ReadInternalAsync();
            var result = mutate(data);
            await WriteInternalAsync(data);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------- 内部 ----------------

    private async Task<AppData> ReadInternalAsync()
    {
        if (!File.Exists(_paths.DataFile))
        {
            // 主文件没了（可能上次正好卡在替换中间）→ 试备份
            var fromBackup = TryReadFile(BackupFile);
            if (fromBackup != null)
            {
                LastLoadWarning ??= "未找到 data.json，已从备份 data.json.bak 恢复。";
                TryPersist(fromBackup);          // 立刻把主文件补回去
                return fromBackup;
            }
            return new AppData();
        }

        string text;
        try { text = await File.ReadAllTextAsync(_paths.DataFile); }
        catch { return new AppData(); }

        if (TryDeserialize(text, out var data)) return data;

        // 损坏：先留证，再试备份
        var preserved = PreserveCorruptFile();
        var backup = TryReadFile(BackupFile);
        if (backup != null)
        {
            LastLoadWarning ??= $"data.json 已损坏，已从备份 data.json.bak 恢复（损坏文件保留为 {preserved}）。";
            TryPersist(backup);
            return backup;
        }

        LastLoadWarning ??= $"data.json 已损坏且没有可用备份，已重置为空配置（损坏文件保留为 {preserved}）。";
        return new AppData();
    }

    private async Task WriteInternalAsync(AppData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DataFile)!);
        var json = JsonSerializer.Serialize(data, JsonOpts.Write);

        // 原子替换：先写临时文件，再整体换上去（同卷内 File.Replace 是原子的）
        var tmp = _paths.DataFile + ".tmp";
        await File.WriteAllTextAsync(tmp, json);

        if (File.Exists(_paths.DataFile))
        {
            try
            {
                File.Replace(tmp, _paths.DataFile, BackupFile, ignoreMetadataErrors: true);
                return;
            }
            catch
            {
                // 某些文件系统 / 被占用时会失败 → 退回普通覆盖
            }
        }
        File.Move(tmp, _paths.DataFile, overwrite: true);
    }

    /// <summary>读取并解析某个文件；不存在或解析失败返回 null。</summary>
    private static AppData? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return TryDeserialize(File.ReadAllText(path), out var data) ? data : null;
        }
        catch { return null; }
    }

    private static bool TryDeserialize(string text, out AppData data)
    {
        data = new AppData();
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<AppData>(text, JsonOpts.Read);
            if (parsed == null) return false;
            data = parsed;
            return true;
        }
        catch { return false; }
    }

    /// <summary>把损坏的 data.json 改名留证（返回新文件名，供提示用）。</summary>
    private string PreserveCorruptFile()
    {
        try
        {
            var dst = $"{_paths.DataFile}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_paths.DataFile, dst, overwrite: true);
            return Path.GetFileName(dst);
        }
        catch { return "(另存失败)"; }
    }

    /// <summary>尽力把恢复出来的数据写回主文件（失败不影响本次读取的结果）。</summary>
    private void TryPersist(AppData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.DataFile)!);
            File.WriteAllText(_paths.DataFile, JsonSerializer.Serialize(data, JsonOpts.Write));
        }
        catch { }
    }
}
