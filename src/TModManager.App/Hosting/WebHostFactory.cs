// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TModManager.Core.Captures;
using TModManager.Core.Configuration;
using TModManager.Core.Logging;
using TModManager.Core.Logs;
using TModManager.Core.Mods;
using TModManager.Core.ModVersions;
using TModManager.Core.Network;
using TModManager.Core.Processes;
using TModManager.Core.Saves;
using TModManager.Core.Snapshots;
using TModManager.Core.Stats;
using TModManager.Core.Storage;

namespace TModManager.App.Hosting;

/// <summary>组装内嵌 Kestrel：随机本地端口 + 静态文件（wwwroot）+ /api/* 路由。</summary>
public static class WebHostFactory
{
    public static async Task<WebHostHandle> StartAsync(AppPaths paths, WindowControlService? windowControl = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebHostFactory).Assembly.GetName().Name ?? "tModManager",
            ContentRootPath = paths.BaseDir,
            // 直接在 options 里指定 web root，避免 CreateBuilder 之后 UseWebRoot 改配置抛 NotSupportedException
            WebRootPath = paths.WwwRoot,
        });

        // 127.0.0.1 随机空闲端口（ListenLocalhost(0) 不支持动态端口，改用显式 Loopback）
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(System.Net.IPAddress.Loopback, 0);
            // 模组版本归档走「把拖进来的 .tmod 原样 POST 上来」这条路 —— WebView2 拿不到文件真实路径
            // （Chromium 没有 Electron 那个 File.path），只能走 HTTP 上传。
            // Kestrel 默认只收 30 MB，而单个 .tmod 轻松上百 MB（本机实测 CalamityMod 114 MB）。
            // 只监听回环、且本服务只被自家页面访问，放宽到 4 GB 是安全的。
            k.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
        });

        AppLog.Init(paths);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<JsonStore>();
        builder.Services.AddSingleton<MetaStore>();
        builder.Services.AddSingleton<WorkshopScanner>();
        builder.Services.AddSingleton<ModsService>();
        builder.Services.AddSingleton<SpecialLoadService>();
        builder.Services.AddSingleton<TmlProcessService>();
        builder.Services.AddSingleton<GitHubService>();
        builder.Services.AddSingleton<TmodUpdateService>();
        builder.Services.AddSingleton<SteamService>();
        builder.Services.AddSingleton<CrashLogService>();
        builder.Services.AddSingleton<SavesService>();
        builder.Services.AddSingleton<StatsService>();
        builder.Services.AddSingleton<SnapshotService>();
        builder.Services.AddSingleton<CapturesService>();
        builder.Services.AddSingleton<ModVersionService>();
        builder.Services.AddHostedService<TmlMonitorService>();
        builder.Services.AddSingleton(windowControl ?? new WindowControlService());

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        var app = builder.Build();

        // 全局异常 → {error} + 状态码（ApiException 携带状态码，其余 500 并记录日志）
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (ApiEndpoints.ApiException ex)
            {
                ctx.Response.StatusCode = ex.StatusCode;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                AppLog.Error($"API {ctx.Request.Method} {ctx.Request.Path} 未处理异常", ex);
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = "服务器内部错误" });
            }
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // 静态资源（图标/前端）本地缓存 1 天：避免每次打开模组列表都重新请求图标，
            // 配合前端 decoding=async 减少 WebView2 软渲染下的重复解码
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "public, max-age=86400";
            },
        });
        ApiEndpoints.Map(app);

        await app.StartAsync();
        return new WebHostHandle(app);
    }
}
