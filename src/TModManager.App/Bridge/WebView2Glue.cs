namespace TModManager.App.Bridge;

/// <summary>注入到页面文档开头的 glue 脚本：把宿主对象包装成前端期望的 window.pywebview.api 形状。</summary>
public static class WebView2Glue
{
    public const string Script = """
        (() => {
          const h = window.chrome?.webview?.hostObjects?.pywebviewHost;
          if (!h) return;
          window.pywebview = {
            api: {
              start_drag: () => h.start_drag(),
              minimize: () => h.minimize(),
              toggle_fullscreen: () => Promise.resolve(!!h.toggle_fullscreen()),
              is_fullscreen: () => Promise.resolve(!!h.is_fullscreen()),
              shutdown: () => h.shutdown(),
            }
          };
        })();
        """;
}
