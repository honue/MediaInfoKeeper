using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace MediaInfoKeeper.Configuration
{
    public class ProxyOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Proxy";

        [DisplayName("启用代理服务器")]
        [Description("开启后所有 HttpClient 请求将走代理。")]
        public bool EnableProxyServer { get; set; } = false;

        [DisplayName("代理服务器地址")]
        [Description("示例：http://user:pass@127.0.0.1:7890 或 socks5://127.0.0.1:1080")]
        public string ProxyServerUrl { get; set; } = "http://127.0.0.1:7890";

        [DisplayName("忽略证书验证")]
        [Description("开启后忽略代理或远端证书错误。")]
        public bool IgnoreCertificateValidation { get; set; } = false;

        [DisplayName("写入环境变量")]
        [Description("同步写入 http_proxy/HTTP_PROXY/HTTPS_PROXY，便于 ffprobe 等外部进程访问需要代理的资源。")]
        public bool WriteProxyEnvVars { get; set; } = true;
    }
}
