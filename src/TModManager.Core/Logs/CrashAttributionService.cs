// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.RegularExpressions;
using TModManager.Core.Models;

namespace TModManager.Core.Logs;

/// <summary>一次游戏退出的日志分析结果。</summary>
public sealed class ExitAnalysis
{
    /// <summary>true = 疑似异常退出（崩溃）；false = 正常退出。</summary>
    public bool Crashed { get; set; }

    /// <summary>一句话结论，例如「缺少前置模组」。</summary>
    public string Summary { get; set; } = "正常退出";

    /// <summary>给用户的处理建议。</summary>
    public string? Advice { get; set; }

    /// <summary>从日志里认出的模组名（可能为空）。</summary>
    public string? ModName { get; set; }

    /// <summary>命中的日志行（最多 4 条，已去重、截断）。</summary>
    public List<string> Evidence { get; set; } = new();

    public string? LogPath { get; set; }
    public string? LogName { get; set; }
}

/// <summary>
/// 崩溃 / 异常退出的自动归因。
///
/// 思路：只读日志尾部（一次会话的 client.log 末尾），依次匹配「缺前置 / 版本不匹配 / 内存不足 /
/// 原生崩溃 / DLL 缺失 / 模组加载失败 / 栈溢出」等特征；同时识别正常退出标记
/// （<c>Saving and quitting</c> / <c>SaveAndQuitCallBack</c> …）。
/// 有正常退出标记且只命中泛化的 <c>Exception</c> 时（很多模组在卸载阶段会抛错），判为正常退出，
/// 避免每次关游戏都误报崩溃。
/// </summary>
public sealed class CrashAttributionService
{
    private sealed record Pattern(string Key, Regex Rx, string Summary, string? Advice, bool Severe);

    private static readonly RegexOptions RxOpts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    /// <summary>按「特异性从高到低」排列：命中第一条就停止，避免被后面的通用 Exception 抢走。</summary>
    private static readonly Pattern[] Patterns =
    {
        new("missing_mod",
            new Regex(@"Missing mod|missing dependency|mod dependency|缺少模组|未找到模组|找不到模组", RxOpts),
            "缺少前置模组",
            "补齐缺失的模组；或在「模组版本」里回滚到依赖齐全的版本。", true),

        new("tmod_version",
            new Regex(@"was built for tModLoader|built for tModLoader|requires tModLoader|需要.{0,12}tModLoader", RxOpts),
            "模组与 tModLoader 版本不匹配",
            "升级 / 降级 tModLoader，或换用与该版本匹配的模组版本。", true),

        new("oom",
            new Regex(@"OutOfMemoryException|Out of memory|内存不足", RxOpts),
            "内存不足",
            "关闭后台程序，或减少同时启用的大型模组。", true),

        new("stack",
            new Regex(@"StackOverflowException|Stack overflow|栈溢出", RxOpts),
            "栈溢出",
            "多半是某个模组递归调用导致，尝试逐个禁用可疑模组。", true),

        new("native",
            new Regex(@"AccessViolationException|0xC0000005|EXCEPTION_ACCESS_VIOLATION|Fatal Error|致命错误", RxOpts),
            "原生层崩溃（显卡驱动 / 非托管代码）",
            "更新显卡驱动；若只在某个模组下出现，尝试禁用该模组。", true),

        new("dll",
            new Regex(@"BadImageFormatException|DllNotFoundException|could not load file or assembly|未能加载文件或程序集|找不到指定的模块", RxOpts),
            "缺少 DLL 或架构不匹配",
            "确认模组与依赖完整，且没有混用 32 位组件。", true),

        new("mod_disabled",
            new Regex(@"has been disabled due to errors|该模组已被禁用|加载时发生错误|Exception while loading mod|Error loading mod|Failed to load mod", RxOpts),
            "模组加载失败",
            "打开崩溃日志查看具体模组，换用兼容版本或暂时禁用。", true),

        new("incompatible",
            new Regex(@"MissingMethodException|MissingFieldException|TypeLoadException|TypeInitializationException", RxOpts),
            "模组与当前 tModLoader / 游戏版本不兼容",
            "更新相关模组，或回滚到与当前 tModLoader 匹配的版本。", true),

        new("file",
            new Regex(@"FileNotFoundException|The system cannot find the file|找不到文件", RxOpts),
            "缺少文件",
            "检查游戏与模组文件是否完整（Steam 可校验文件完整性）。", false),

        new("unhandled",
            new Regex(@"Unhandled exception|未处理的异常", RxOpts),
            "未处理的异常",
            "查看崩溃日志中的第一段异常堆栈，通常能定位到具体模组。", true),

        new("generic",
            new Regex(@"\bException\b|\[ERROR\]", RxOpts),
            "日志中出现错误",
            "打开崩溃日志查看详情。", false),
    };

    /// <summary>正常退出的标记：命中这些说明游戏是走正常关流程的。</summary>
    private static readonly string[] CleanExitMarkers =
    {
        "Saving and quitting",
        "正在保存并退出",
        "SaveAndQuitCallBack",
        "Main.OnExit",
    };

    private static readonly HashSet<string> ModTagNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "tML", "Terraria", "Main Thread", "StatusText", "FNA", "ReLogic", "ModLoader", ".NET TP Worker",
    };

    private static readonly Regex ModTagRx = new(@"\]\s*\[([A-Za-z][A-Za-z0-9_.\- ]{1,48})\]\s*:", RxOpts);

    private readonly CrashLogService _logs;

    public CrashAttributionService(CrashLogService logs) => _logs = logs;

    /// <summary>
    /// 分析「本次会话」写入的日志。找不到（或日志太旧、与本次会话无关）时返回 null。
    /// </summary>
    /// <param name="since">本次会话开始时间（UTC）；用于挑出这次会话写的日志。</param>
    public ExitAnalysis? AnalyzeLatest(AppData data, DateTimeOffset? since = null)
    {
        var files = _logs.List(data);
        if (files.Count == 0) return null;

        var sinceLocal = since?.ToLocalTime().DateTime;

        LogFileEntry? chosen = null;
        if (sinceLocal is { } s)
        {
            chosen = files.FirstOrDefault(f =>
                         f.Name.Equals("client.log", StringComparison.OrdinalIgnoreCase) && f.Modified >= s.AddSeconds(-30))
                     ?? files.FirstOrDefault(f => f.Modified >= s.AddSeconds(-30));
        }

        chosen ??= files.FirstOrDefault(f => f.Name.Equals("client.log", StringComparison.OrdinalIgnoreCase))
                  ?? files[0];

        // 日志最后写入时间比会话开始还早 5 分钟以上 ⇒ 不是这次会话的日志，别拿来误报
        if (sinceLocal is { } s2 && chosen.Modified < s2.AddMinutes(-5)) return null;

        var content = _logs.Read(data, chosen.Path);
        if (content == null) return null;

        return AnalyzeText(content.Text, content.Path, content.Name);
    }

    /// <summary>纯文本分析（便于单元测试）。</summary>
    public static ExitAnalysis AnalyzeText(string text, string? logPath = null, string? logName = null)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var from = Math.Max(0, lines.Length - 800);          // 只看尾部：崩溃现场总在最后
        var tail = lines[from..];
        var tailText = string.Join("\n", tail);

        var cleanExit = CleanExitMarkers.Any(m => tailText.Contains(m, StringComparison.OrdinalIgnoreCase));

        Pattern? hit = null;
        string? hitLine = null;
        var hitIndex = -1;

        foreach (var p in Patterns)
        {
            for (var i = tail.Length - 1; i >= 0; i--)
            {
                if (tail[i].Length == 0) continue;
                if (!p.Rx.IsMatch(tail[i])) continue;
                hit = p;
                hitLine = tail[i];
                hitIndex = i;
                break;
            }
            if (hit != null) break;
        }

        // 正常退出标记 + 只命中泛化错误 ⇒ 多半是模组在卸载阶段抛的杂音，不算崩溃
        var crashed = hit != null && (!cleanExit || hit.Severe);

        var result = new ExitAnalysis
        {
            Crashed = crashed,
            LogPath = logPath,
            LogName = logName,
            Summary = crashed ? hit!.Summary : "正常退出",
            Advice = crashed ? hit!.Advice : null,
            ModName = crashed && hitLine != null ? ExtractModName(hitLine) : null,
        };

        if (crashed)
        {
            result.Evidence = CollectEvidence(tail, hitIndex);
        }

        return result;
    }

    /// <summary>收集命中的日志行（从最近往前，最多 4 条，去重）。</summary>
    private static List<string> CollectEvidence(string[] tail, int hitIndex)
    {
        var list = new List<string>();
        void Add(int i)
        {
            if (i < 0 || i >= tail.Length) return;
            var line = tail[i].Trim();
            if (line.Length == 0) return;
            if (line.Length > 400) line = line[..400] + "…";
            if (list.Contains(line)) return;
            list.Add(line);
        }

        Add(hitIndex);
        for (var i = hitIndex + 1; i < tail.Length && list.Count < 4; i++)
            if (Patterns.Any(p => p.Rx.IsMatch(tail[i])))
                Add(i);

        // 命中行之后没有更多错误行时，往前补几行上下文
        for (var i = hitIndex - 1; i >= 0 && list.Count < 4; i--)
            if (tail[i].Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                tail[i].Contains("Exception", StringComparison.OrdinalIgnoreCase))
                Add(i);

        return list;
    }

    /// <summary>从形如 <c>[...] [模组名/ERROR] [CalamityMod]: ...</c> 的行里认出模组名。</summary>
    private static string? ExtractModName(string line)
    {
        foreach (Match m in ModTagRx.Matches(line))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0) continue;
            if (ModTagNoise.Contains(name)) continue;
            return name;
        }
        return null;
    }
}
