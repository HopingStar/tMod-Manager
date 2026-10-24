// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.App.Hosting;
using TModManager.Core.Logs;

namespace TModManager.Tests;

/// <summary>
/// 崩溃自动归因：覆盖「缺前置 / 内存不足 / 版本不匹配」等特征，
/// 以及「正常退出时模组在卸载阶段抛的杂音不算崩溃」这条容易误报的规则。
/// </summary>
public class CrashAttributionTests
{
    private const string Header = "[12:00:00] [Main Thread/INFO] [tML]: Starting tModLoader client 1.4.4.9\n";

    [Fact]
    public void MissingMod_IsAttributed()
    {
        var text = Header +
                   "[12:00:10] [Main Thread/ERROR] [CalamityOverhaul]: Missing mod: CalamityModMusic\n";

        var a = CrashAttributionService.AnalyzeText(text, @"C:\logs\client.log", "client.log");

        Assert.True(a.Crashed);
        Assert.Contains("缺少前置模组", a.Summary);
        Assert.Equal("CalamityOverhaul", a.ModName);
        Assert.Equal("client.log", a.LogName);
        Assert.NotEmpty(a.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(a.Advice));
    }

    [Fact]
    public void OutOfMemory_IsAttributed()
    {
        var text = Header +
                   "[12:00:10] [Main Thread/ERROR] [tML]: System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.True(a.Crashed);
        Assert.Contains("内存不足", a.Summary);
    }

    [Fact]
    public void TmodVersionMismatch_IsAttributed()
    {
        var text = Header +
                   "[12:00:10] [Main Thread/ERROR] [tML]: The mod 'Foo' was built for tModLoader v2025.1.0.0\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.True(a.Crashed);
        Assert.Contains("版本不匹配", a.Summary);
    }

    [Fact]
    public void CleanExit_WithShutdownNoise_IsNotCrash()
    {
        // 真实场景：用户正常退出，某个模组在 OnWorldUnload 阶段抛异常并打 ERROR —— 不能报「崩溃」
        var text = Header +
                   "[12:30:00] [.NET TP Worker/INFO] [StatusText]: 正在保存并退出...\n" +
                   "[12:30:01] [.NET TP Worker/ERROR] [InnoVault]: Stage [OnWorldUnload] failed: boom\n" +
                   "[12:30:01] [.NET TP Worker/WARN] [tML]: 静默捕获异常:\n" +
                   "System.Exception: something went wrong\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.False(a.Crashed);
        Assert.Equal("正常退出", a.Summary);
        Assert.Empty(a.Evidence);
    }

    [Fact]
    public void CleanExitMarker_DoesNotHideSevereErrors()
    {
        // 有「保存并退出」但仍出现致命错误（比如缺前置）⇒ 仍判崩溃
        var text = Header +
                   "[12:30:00] [.NET TP Worker/INFO] [StatusText]: 正在保存并退出...\n" +
                   "[12:30:01] [Main Thread/ERROR] [tML]: Missing mod dependency: CalamityMod\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.True(a.Crashed);
        Assert.Contains("缺少前置模组", a.Summary);
    }

    [Fact]
    public void QuietLog_IsNormalExit()
    {
        var text = Header + "[12:30:00] [Main Thread/INFO] [tML]: 正在卸载模组...\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.False(a.Crashed);
        Assert.Equal("正常退出", a.Summary);
    }

    [Fact]
    public void Evidence_IsDeduplicatedAndTrimmed()
    {
        var longLine = new string('x', 600);
        var text = Header +
                   "[12:00:10] [Main Thread/ERROR] [Foo]: Missing mod: Bar " + longLine + "\n";

        var a = CrashAttributionService.AnalyzeText(text);

        Assert.True(a.Crashed);
        Assert.Single(a.Evidence);
        Assert.True(a.Evidence[0].Length <= 401);   // 400 + 省略号
    }

    [Fact]
    public void DurationFormatting_IsReadable()
    {
        Assert.Equal("0 秒", ExitReportService.FormatDuration(0));
        Assert.Equal("45 秒", ExitReportService.FormatDuration(45));
        Assert.Equal("2 分钟", ExitReportService.FormatDuration(120));
        Assert.Equal("12 分 5 秒", ExitReportService.FormatDuration(725));
        Assert.Equal("1 小时 23 分", ExitReportService.FormatDuration(5000));
    }
}
