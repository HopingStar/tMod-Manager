// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

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
    private const string SingleInstanceMutexName = "tModManager.SingleInstance";
    private const string ShowInstanceEventName = "tModManager.ShowInstance.v1";

    private WebHostHandle? _host;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1) 解析路径（数据与前端）
        //    单文件版里 AppContext.BaseDirectory 是运行时解压目录，
        //    数据要落在用户双击的那个 exe 旁边（Environment.ProcessPath）。
        var realExeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var paths = AppPaths.Resolve(AppContext.BaseDirectory, realExeDir);
        AppPaths.Current = paths;
        AppLog.Init(paths);
        AppLog.Info($"启动 tModManager：数据目录={paths.BaseDir}；前端目录={paths.WwwRoot}");

        // 2) 单实例：已有实例时**不再强杀**它，而是请求它把窗口显示出来，然后本实例退出。
        //    旧实现会 Kill 正在运行的实例 —— 如果启动器被设成「启动后隐藏」，
        //    用户再点一次图标就会把隐藏中的启动器直接杀掉，看起来像「启动器被强制关闭」。
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            if (TrySignalExistingInstance())
                AppLog.Info("检测到已有实例，已请求其显示窗口，本实例退出");
            else
            {
                MessageBox.Show("tModManager 已在运行。请从任务栏找到它。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        StartShowListener();

        // 3) 首启预置（只在第一个实例里做）
        new FirstRunProvisioner(paths).Provision();

        // 4) 启动内嵌 Kestrel（随机端口）
        var wcs = new WindowControlService();
        _host = await WebHostFactory.StartAsync(paths, wcs);

        // 5) 建窗并显示（与图片查看器一致：直接 Show + Activate，不做 Hide/Show ——
        //    Hide 会干扰 WindowChrome 的 resize 命中初始化）
        //    恢复上次保存的窗口缩放尺寸（data.json window_size，无则用 XAML 默认）
        var savedSize = (await new JsonStore(paths).ReadAsync())?.WindowSize;
        var window = new MainWindow(paths, _host.Port, wcs, savedSize);
        window.Show();
        window.Activate();
    }

    /// <summary>把「显示窗口」的信号发给已经运行的实例。成功返回 true。</summary>
    private static bool TrySignalExistingInstance()
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowInstanceEventName);
                ev.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(150);   // 老实例可能刚启动，监听线程还没建好
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>监听第二个实例发来的「显示窗口」信号。</summary>
    private void StartShowListener()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowInstanceEventName);
            _showListener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (MainWindow is Wpf.MainWindow w) w.EnsureShown();
                        }));
                    }
                    catch
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "tModManager.ShowListener",
            };
            _showListener.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("创建单实例显示监听失败: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            AppLog.Info("OnExit: 开始停止 Kestrel 宿主");
            try { _host.Stop(); } catch { }
            AppLog.Info("OnExit: Kestrel 已停止");
        }
        else
        {
            AppLog.Info("OnExit: 未启动宿主（单实例退出）");
        }

        try { _showEvent?.Dispose(); } catch { }
        _showEvent = null;
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        base.OnExit(e);
    }
}
