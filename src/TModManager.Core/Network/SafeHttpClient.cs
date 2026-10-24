// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Net;

namespace TModManager.Core.Network;

/// <summary>只读公开数据的 HTTP 客户端。
/// 本机 SSL 证书链/主机名可能异常（参考 NuGet 镜像证书不匹配问题），对齐 Python net_fetch.steam_request 的
/// verify=False 降级：证书校验失败时放行。本地工具只读公开数据，风险可接受。</summary>
public static class SafeHttpClient
{
    private static HttpClient? _http;

    public static HttpClient Instance
    {
        get
        {
            if (_http == null)
            {
                var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                };
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tmod-manager");
                _http = client;
            }
            return _http;
        }
    }
}
