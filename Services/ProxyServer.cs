using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public static class ProxyServer
    {
        private static readonly string[] BypassAddressList =
        {
            @"^10\.\d{1,3}\.\d{1,3}\.\d{1,3}$",
            @"^172\.(1[6-9]|2[0-9]|3[0-1])\.\d{1,3}\.\d{1,3}$",
            @"^192\.168\.\d{1,3}\.\d{1,3}$"
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo createHttpClientHandler;
        private static bool isEnabled;
        private static bool isPatched;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var applicationHost =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.ApplicationHost");
                createHttpClientHandler = applicationHost?.GetMethod("CreateHttpClientHandler",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (createHttpClientHandler == null)
                {
                    logger?.Warn("代理服务器初始化失败：CreateHttpClientHandler 未找到。");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.proxy");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("代理服务器初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            ApplyProxyEnvironmentVariables();

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }

            logger?.Info("代理服务器 " + (isEnabled ? "已启用" : "已禁用"));
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(createHttpClientHandler,
                postfix: new HarmonyMethod(typeof(ProxyServer), nameof(CreateHttpClientHandlerPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(createHttpClientHandler, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void CreateHttpClientHandlerPostfix(ref HttpMessageHandler __result)
        {
            if (!isEnabled)
            {
                return;
            }

            var options = Plugin.Instance.Options.Proxy;
            if (options == null)
            {
                return;
            }

            if (!TryParseProxyUrl(options.ProxyServerUrl, out var proxyUri, out var credentials))
            {
                return;
            }

            var proxy = new WebProxy(proxyUri)
            {
                BypassProxyOnLocal = true,
                BypassList = BypassAddressList,
                Credentials = credentials
            };

            if (__result is HttpClientHandler httpClientHandler)
            {
                httpClientHandler.Proxy = proxy;
                httpClientHandler.UseProxy = true;
                if (options.IgnoreCertificateValidation)
                {
                    httpClientHandler.ServerCertificateCustomValidationCallback =
                        (httpRequestMessage, cert, chain, sslErrors) => true;
                }
            }
            else if (__result is SocketsHttpHandler socketsHttpHandler)
            {
                socketsHttpHandler.Proxy = proxy;
                socketsHttpHandler.UseProxy = true;
                if (options.IgnoreCertificateValidation)
                {
                    socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback =
                        (sender, cert, chain, sslErrors) => true;
                }
            }
        }

        private static bool TryParseProxyUrl(string raw, out Uri proxyUri, out NetworkCredential credentials)
        {
            proxyUri = null;
            credentials = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            {
                logger?.Warn("代理服务器地址无效: {0}", raw);
                return false;
            }

            proxyUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                if (!string.IsNullOrWhiteSpace(parts[0]))
                {
                    credentials = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
                }
            }

            return true;
        }

        private static void ApplyProxyEnvironmentVariables()
        {
            var options = Plugin.Instance.Options.Proxy;
            var proxyUrl = options?.ProxyServerUrl?.Trim() ?? string.Empty;
            var writeEnv = options?.WriteProxyEnvVars == true;

            if (isEnabled && writeEnv && !string.IsNullOrEmpty(proxyUrl))
            {
                Environment.SetEnvironmentVariable("http_proxy", proxyUrl);
                Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl);
                logger.Info($"设置代理环境变量 {proxyUrl}");
            }
        }
    }
}
