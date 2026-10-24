// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TModManager.Core.Logging;

namespace TModManager.App.Hosting;

/// <summary>内嵌 Kestrel 宿主的句柄。</summary>
public sealed class WebHostHandle
{
    public Microsoft.AspNetCore.Builder.WebApplication App { get; }
    public int Port { get; }

    public WebHostHandle(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        App = app;
        Port = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Select(a => new Uri(a).Port)
            .First();
    }

    public void Stop()
    {
        try
        {
            // StopAsync/DisposeAsync 必须在后台线程等待：async 方法捕获调用点的 SynchronizationContext，
            // 若在 WPF 主线程（DispatcherSynchronizationContext）上 .GetResult()，其 continuation 要回
            // 主线程续跑，而主线程被阻塞 → 永久死锁（关窗/退出卡死）。Task.Run 移出主线程即可解开。
            var stop = Task.Run(async () => await App.StopAsync());
            if (!stop.Wait(TimeSpan.FromSeconds(5)))
            {
                AppLog.Warn("Kestrel 优雅停止超时，直接退出");
                return;
            }
            var dispose = Task.Run(async () => await App.DisposeAsync());
            dispose.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            AppLog.Warn("停止宿主异常: " + ex.Message);
        }
    }
}
