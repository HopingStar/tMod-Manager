using System.Diagnostics;
using System.Threading;
using System.Windows;
using TModManager.App.Hosting;
using TModManager.App.Wpf;
using TModManager.Core.Configuration;
using TModManager.Core.Logging;
using TModManager.Core.Storage;

namespace TModManager.App;

public partial class App : Application
{
    private WebHostHandle? _host;
    private Mutex? _instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0) 单实例互斥：已有实例时自动终止它（可能是旧代码运行中的窗口），确保本次启动生效
        _instanceMutex = new Mutex(true, "tModManager.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("tModManager"))
            {
                if (p.Id != self)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }
            _instanceMutex.Dispose();
            _instanceMutex = new Mutex(true, "tModManager.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("tModManager 已在运行，请先关闭后再启动。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        // 1) 解析路径 + 首启预置（数据与前端）
        var paths = AppPaths.Resolve(AppContext.BaseDirectory);
        AppPaths.Current = paths;
        AppLog.Init(paths);
        new FirstRunProvisioner(paths).Provision();

        // 2) 启动内嵌 Kestrel（随机端口）
        var wcs = new WindowControlService();
        _host = await WebHostFactory.StartAsync(paths, wcs);

        // 3) 建窗并显示（与图片查看器一致：直接 Show + Activate，不做 Hide/Show ——
        //    Hide 会干扰 WindowChrome 的 resize 命中初始化）
        //    恢复上次保存的窗口缩放尺寸（data.json window_size，无则用 XAML 默认）
        var savedSize = (await new JsonStore(paths).ReadAsync())?.WindowSize;
        var window = new MainWindow(paths, _host.Port, wcs, savedSize);
        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("OnExit: 开始停止 Kestrel 宿主");
        try { _host?.Stop(); } catch { }
        AppLog.Info("OnExit: Kestrel 已停止");
        base.OnExit(e);
    }
}
