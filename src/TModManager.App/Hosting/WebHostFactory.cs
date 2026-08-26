using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TModManager.Core.Configuration;
using TModManager.Core.Logging;
using TModManager.Core.Mods;
using TModManager.Core.Network;
using TModManager.Core.Processes;
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
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));

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
