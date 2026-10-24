// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Configuration;

namespace TModManager.Core.Logging;

/// <summary>轻量日志：追加写 BaseDir/logs/tmod_manager.log，超 1MB 轮转为 .old。线程安全，失败静默。</summary>
public static class AppLog
{
    private static string? _dir;
    private static readonly object Lock = new();

    public static void Init(AppPaths paths)
    {
        lock (Lock) _dir = Path.Combine(paths.BaseDir, "logs");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex is null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        lock (Lock)
        {
            if (_dir == null) return;
            try
            {
                Directory.CreateDirectory(_dir);
                var file = Path.Combine(_dir, "tmod_manager.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                {
                    var old = Path.Combine(_dir, "tmod_manager.log.old");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
