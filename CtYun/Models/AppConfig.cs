using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class AppConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountConfig> Accounts { get; set; } = [];

        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;

        // 随机保活区间(秒)。当二者都 > 0 时启用随机，每次重连从 [min, max] 取值；
        // 未配置(默认 0)时退回固定值 keepAliveSeconds，行为与旧版一致。
        [JsonPropertyName("keepAliveMinSeconds")]
        public int KeepAliveMinSeconds { get; set; }

        [JsonPropertyName("keepAliveMaxSeconds")]
        public int KeepAliveMaxSeconds { get; set; }

    }

    public class AccountConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }
    }
}
