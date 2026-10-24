// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Management;

namespace TModManager.Core.Processes;

/// <summary>通过 WMI (Win32_Process) 读取进程命令行。</summary>
public static class ProcessCommandLineReader
{
    public static string? Get(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    return obj["CommandLine"]?.ToString();
                }
            }
        }
        catch
        {
        }
        return null;
    }
}
