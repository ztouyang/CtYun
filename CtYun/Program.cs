using CtYun;
using CtYun.Models;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");

var runtimeConfig = LoadRuntimeConfig();
if (runtimeConfig.Accounts.Count == 0)
{
    Utility.WriteLine(ConsoleColor.Red, "未读取到账号配置。请配置 accounts.json，或设置 APP_USER/APP_PASSWORD，或使用交互输入模式。");
    return;
}

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    globalCts.Cancel();
};

var sessionTasks = runtimeConfig.Accounts.Select(account => RunAccountAsync(account, runtimeConfig, globalCts.Token));

try
{
    await Task.WhenAll(sessionTasks);
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}

async Task RunAccountAsync(AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    var label = AccountLabel(account);
    var api = new CtYunApi(account.DeviceCode);

    Utility.WriteLine(ConsoleColor.Cyan, $"[{label}] 开始登录。");
    if (!await PerformLoginSequence(api, account, runtimeConfig, ct))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 登录失败，跳过该账号。");
        return;
    }

    var desktopList = await api.GetLlientListAsync();
    if (desktopList == null || desktopList.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 未获取到云电脑。");
        return;
    }

    var activeDesktops = new List<Desktop>();
    foreach (var desktop in desktopList)
    {
        if (desktop.UseStatusText != "运行中")
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] [{desktop.UseStatusText}] 电脑未开机，正在开机，请在2分钟后重新运行软件。");
        }

        var connectResult = await api.ConnectAsync(desktop.DesktopId);
        if (connectResult.Success && connectResult.Data?.DesktopInfo != null)
        {
            desktop.DesktopInfo = connectResult.Data.DesktopInfo;
            activeDesktops.Add(desktop);
        }
        else
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] Connect Error: [{desktop.DesktopId}] {connectResult.Msg}");
        }
    }

    if (activeDesktops.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 没有可保活的云电脑。");
        return;
    }

    var keepAliveHint = runtimeConfig.KeepAliveMinSeconds == runtimeConfig.KeepAliveMaxSeconds
        ? $"每 {runtimeConfig.KeepAliveMinSeconds} 秒"
        : $"每 {runtimeConfig.KeepAliveMinSeconds}~{runtimeConfig.KeepAliveMaxSeconds} 秒随机";
    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：{keepAliveHint}强制重连一次。");
    var keepAliveTasks = activeDesktops.Select(d => KeepAliveWorkerWithForcedReset(api, account, d, runtimeConfig.KeepAliveMinSeconds, runtimeConfig.KeepAliveMaxSeconds, ct));
    await Task.WhenAll(keepAliveTasks);
}

async Task<bool> PerformLoginSequence(CtYunApi api, AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    if (!await api.LoginAsync(account.User, account.Password))
    {
        return false;
    }

    if (api.LoginInfo.BondedDevice)
    {
        return true;
    }

    var label = AccountLabel(account);
    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 当前设备未绑定，正在发送短信验证码。");
    if (!await api.GetSmsCodeAsync(account.User))
    {
        return false;
    }

    var verificationCode = ReadVerificationCode(account);
    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 未获取到短信验证码。");
        return false;
    }

    return await api.BindingDeviceAsync(verificationCode.Trim());
}

string ReadVerificationCode(AccountConfig account)
{
    var label = AccountLabel(account);
    if (!CanReadFromConsole())
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 当前账号需要短信验证码，请使用 -it 交互模式重新运行并输入验证码。");
        return "";
    }

    Console.Write($"[{label}] 短信验证码: ");
    return Console.ReadLine();
}

async Task KeepAliveWorkerWithForcedReset(CtYunApi api, AccountConfig account, Desktop desktop, int minSeconds, int maxSeconds, CancellationToken globalToken)
{
    var label = AccountLabel(account);
    var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");
    var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

    while (!globalToken.IsCancellationRequested)
    {
        // 每个周期随机一个保活时长；Next 上界为开区间，+1 让 maxSeconds 可被取到
        var keepAliveSeconds = minSeconds >= maxSeconds
            ? minSeconds
            : Random.Shared.Next(minSeconds, maxSeconds + 1);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        sessionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveSeconds));

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
        client.Options.AddSubProtocol("binary");

        try
        {
            Utility.WriteLine(ConsoleColor.Cyan, $"[{label}][{desktop.DesktopCode}] === 新周期开始，尝试连接 ===");
            await client.ConnectAsync(uri, sessionCts.Token);

            var hostParts = desktop.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
            var connectMessage = new ConnecMessage
            {
                type = 1,
                ssl = 1,
                host = hostParts[0],
                port = hostParts.Length > 1 ? hostParts[1] : "443",
                ca = desktop.DesktopInfo.CaCert,
                cert = desktop.DesktopInfo.ClientCert,
                key = desktop.DesktopInfo.ClientKey,
                servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                oqs = 0
            };

            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
            await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

            await Task.Delay(500, sessionCts.Token);
            await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] 连接已就绪，保持 {keepAliveSeconds} 秒...");

            try
            {
                await ReceiveLoop(api, client, account, desktop, sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 周期时间到，准备重连...");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 异常: {ex.Message}");
            await Task.Delay(5000, globalToken);
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", CancellationToken.None);
            }
        }
    }
}

async Task ReceiveLoop(CtYunApi api, ClientWebSocket ws, AccountConfig account, Desktop desktop, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();
    var label = AccountLabel(account);

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) break;

        if (result.Count == 0)
        {
            continue;
        }

        var data = buffer.AsSpan(0, result.Count).ToArray();
        var hex = BitConverter.ToString(data).Replace("-", "");
        if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
        {
            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] -> 收到保活校验");
            var response = encryptor.Execute(data);
            await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
            Utility.WriteLine(ConsoleColor.DarkGreen, $"[{label}][{desktop.DesktopCode}] -> 发送保活响应成功");
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var info in infos)
            {
                if (info.Type == 103)
                {
                    var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + api.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + api.LoginInfo.UserId + "}");
                    var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 消息解析失败: {ex.Message}");
        }
    }
}

RuntimeConfig LoadRuntimeConfig()
{
    var dataDir = GetDataDir();
    Directory.CreateDirectory(dataDir);

    var config = LoadAccountsFromFile(dataDir) ?? LoadAccountsFromEnvironment();
    if (config == null || config.Accounts.Count == 0)
    {
        config = LoadAccountsFromConsole(dataDir);
    }

    foreach (var account in config.Accounts)
    {
        account.Name = FirstNotEmpty(account.Name, account.User);
        account.DeviceCode = ResolveDeviceCode(account, dataDir);
    }

    // 未配置随机区间(值 <= 0)时退回固定值 keepAliveSeconds
    var minSeconds = config.KeepAliveMinSeconds > 0 ? config.KeepAliveMinSeconds : config.KeepAliveSeconds;
    var maxSeconds = config.KeepAliveMaxSeconds > 0 ? config.KeepAliveMaxSeconds : config.KeepAliveSeconds;
    minSeconds = Math.Max(10, minSeconds);   // 不低于 10 秒
    maxSeconds = Math.Max(minSeconds, maxSeconds); // 保证 max >= min

    return new RuntimeConfig(config.Accounts, minSeconds, maxSeconds, dataDir);
}

AppConfig LoadAccountsFromEnvironment()
{
    var user = Environment.GetEnvironmentVariable("APP_USER");
    var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    return new AppConfig
    {
        Accounts =
        [
            new AccountConfig
            {
                Name = Environment.GetEnvironmentVariable("APP_NAME"),
                User = user,
                Password = password,
                DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
            }
        ]
    };
}

AppConfig LoadAccountsFromFile(string dataDir)
{
    var configPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(dataDir, "accounts.json");
    }

    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
        Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{configPath}");
        return config;
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
        return null;
    }
}

AppConfig LoadAccountsFromConsole(string dataDir)
{
    if (!CanReadFromConsole())
    {
        return new AppConfig();
    }

    var accounts = new List<AccountConfig>();
    while (true)
    {
        Console.Write("账号: ");
        var user = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(user))
        {
            break;
        }

        Console.Write("密码: ");
        var password = ReadPassword();
        accounts.Add(new AccountConfig { Name = user, User = user, Password = password });

        Console.Write("继续添加账号? (y/N): ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
    }

    if (accounts.Count > 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"交互输入模式已读取 {accounts.Count} 个账号。设备码会保存到 {Path.Combine(dataDir, "devices")}。");
    }

    return new AppConfig { Accounts = accounts };
}

string ResolveDeviceCode(AccountConfig account, string dataDir)
{
    if (!string.IsNullOrWhiteSpace(account.DeviceCode))
    {
        return account.DeviceCode.Trim();
    }

    var devicesDir = Path.Combine(dataDir, "devices");
    Directory.CreateDirectory(devicesDir);
    var deviceCodePath = Path.Combine(devicesDir, SafeName(account.Name ?? account.User) + ".txt");
    if (!File.Exists(deviceCodePath))
    {
        File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
    }

    return File.ReadAllText(deviceCodePath).Trim();
}

string GetDataDir()
{
    var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        return dataDir;
    }

    return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
}

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write("*");
        }
    }
}

static string AccountLabel(AccountConfig account) => account.Name ?? account.User;

static string SafeName(string value)
{
    var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
    var builder = new StringBuilder(source.Length);
    foreach (var ch in source)
    {
        builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
    }
    return builder.ToString();
}

static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static bool CanReadFromConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

static bool IsRunningInContainer() => File.Exists("/.dockerenv");

record RuntimeConfig(
    List<AccountConfig> Accounts,
    int KeepAliveMinSeconds,
    int KeepAliveMaxSeconds,
    string DataDir);
