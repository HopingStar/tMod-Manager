// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Diagnostics;

namespace TModManager.Core.Processes;

/// <summary>tModLoader 进程检测（3s 缓存）/ 关闭 / 启动 .bat。</summary>
public sealed class TmlProcessService
{
    private readonly object _lock = new();
    private DateTime _cacheTime = DateTime.MinValue;
    private bool _cacheRunning;
    private const double TtlSeconds = 3.0;

    /// <summary>上一次确认属于 tModLoader 的 PID（用于抵抗 WMI 命令行读取的短暂失败）。</summary>
    private readonly List<int> _knownPids = new();

    /// <summary>是否在运行（带 3s 缓存；关键操作传 force=true）。</summary>
    public bool IsRunning(bool force = false)
    {
        lock (_lock)
        {
            if (!force && (DateTime.UtcNow - _cacheTime).TotalSeconds < TtlSeconds)
                return _cacheRunning;
            var running = Detect();
            _cacheTime = DateTime.UtcNow;
            _cacheRunning = running;
            return running;
        }
    }

    /// <summary>找到所有 tModLoader 进程 PID。</summary>
    public List<int> FindPids()
    {
        var result = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            var name = p.ProcessName.ToLowerInvariant();
            if (name is not ("dotnet" or "tmodloader")) continue;
            try
            {
                if (TmlProcessMatcher.IsTmodLoaderProc(name, ProcessCommandLineReader.Get(p.Id)))
                    result.Add(p.Id);
            }
            catch
            {
                // AccessDenied 等：静默跳过
            }
        }
        return result;
    }

    /// <summary>强制关闭所有 tModLoader 进程（含进程树）。返回是否杀到了。</summary>
    public bool Kill()
    {
        var killed = false;
        foreach (var pid in FindPids())
        {
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                killed = true;
            }
            catch
            {
            }
        }
        return killed;
    }

    /// <summary>用 .bat 启动 tML（UseShellExecute 走 shell 关联，游戏窗口正常显示）。</summary>
    public static void LaunchBat(string batPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = batPath,
            WorkingDirectory = Path.GetDirectoryName(batPath) ?? "",
            UseShellExecute = true,
        };
        Process.Start(psi);
    }

    /// <summary>
    /// 检测 tModLoader 进程。
    ///
    /// 先检查「上一次确认过的 PID 是否还活着」—— WMI 读命令行在游戏存档 / GC 高峰时可能短暂失败，
    /// 旧实现会因此返回「未运行」，把一次正常的游戏误判成退出（进而触发特殊加载还原、提前结算统计，
    /// 界面也会误显示「游戏已退出」）。粘住已知 PID 可以避免这种假退出。
    /// </summary>
    private bool Detect()
    {
        foreach (var pid in _knownPids.ToArray())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    _knownPids.Remove(pid);
                    continue;
                }

                var name = p.ProcessName.ToLowerInvariant();
                if (name is "dotnet" or "tmodloader") return true;

                // PID 被系统复用成别的程序了
                _knownPids.Remove(pid);
            }
            catch
            {
                _knownPids.Remove(pid);
            }
        }

        var found = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName.ToLowerInvariant();
                if (name is not ("dotnet" or "tmodloader")) continue;
                if (TmlProcessMatcher.IsTmodLoaderProc(name, ProcessCommandLineReader.Get(p.Id)))
                    found.Add(p.Id);
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        _knownPids.Clear();
        _knownPids.AddRange(found);
        return found.Count > 0;
    }
}
