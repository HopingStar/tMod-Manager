using System.Text.Json;
using TModManager.Core.Configuration;
using TModManager.Core.Models;

namespace TModManager.Core.Storage;

/// <summary>data.json 读写（全局门锁，损坏文件兜底为空数据）。</summary>
public sealed class JsonStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonStore(AppPaths paths) => _paths = paths;

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

    private async Task<AppData> ReadInternalAsync()
    {
        if (!File.Exists(_paths.DataFile)) return new AppData();
        try
        {
            var json = await File.ReadAllTextAsync(_paths.DataFile);
            return JsonSerializer.Deserialize<AppData>(json, JsonOpts.Read) ?? new AppData();
        }
        catch
        {
            return new AppData();
        }
    }

    private async Task WriteInternalAsync(AppData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DataFile)!);
        var json = JsonSerializer.Serialize(data, JsonOpts.Write);
        await File.WriteAllTextAsync(_paths.DataFile, json);
    }
}
