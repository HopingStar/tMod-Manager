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

    private static bool Detect()
    {
        foreach (var p in Process.GetProcesses())
        {
            var name = p.ProcessName.ToLowerInvariant();
            if (name is not ("dotnet" or "tmodloader")) continue;
            try
            {
                if (TmlProcessMatcher.IsTmodLoaderProc(name, ProcessCommandLineReader.Get(p.Id))) return true;
            }
            catch
            {
            }
        }
        return false;
    }
}
