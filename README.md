# CtYun 云电脑保活

CtYun 用于登录天翼云电脑并维持 WebSocket 保活连接。当前版本支持：

- 多账号同时登录和保活。
- 保活间隔可固定，也可设置为随机区间（防止固定节奏被识别）。
- Windows/本机可执行文件：支持本地配置文件模式和交互输入模式。
- Docker：支持挂载本地配置文件模式和 `-it` 交互输入模式。
- 首次绑定设备需要短信验证码时，通过终端交互输入。

## 配置文件模式

程序默认在数据目录读取 `accounts.json`：

- 本机可执行文件：默认数据目录为程序所在目录。
- Docker：默认数据目录为 `/app/data`，建议挂载到宿主机。
- 也可以通过环境变量 `CTYUN_CONFIG` 指定配置文件路径。
- 也可以通过环境变量 `CTYUN_DATA_DIR` 指定数据目录。

`accounts.json` 示例：

```json
{
  "keepAliveSeconds": 60,
  "accounts": [
    {
      "name": "account-a",
      "user": "你的账号1",
      "password": "你的密码1",
      "deviceCode": "web_自行生成的32位随机字符"
    },
    {
      "name": "account-b",
      "user": "你的账号2",
      "password": "你的密码2"
    }
  ]
}
```

`deviceCode` 可不填。程序会为每个账号自动生成设备码，并保存到 `devices/{账号名}.txt`。为了避免每次 Docker 重建镜像后重新绑定设备，务必持久化数据目录。

### 随机保活间隔

在根级别额外配置 `keepAliveMinSeconds` 和 `keepAliveMaxSeconds`，即可让每次强制重连的间隔在该区间内随机取值：

```json
{
  "keepAliveMinSeconds": 45,
  "keepAliveMaxSeconds": 90,
  "accounts": [
    { "name": "account-a", "user": "你的账号", "password": "你的密码" }
  ]
}
```

规则：

- 两者都 > 0 且 `max >= min` 时启用随机，每台云电脑**每次重连**独立取一个 `[min, max]` 内的秒数。
- 只要有一项未配置（默认 0），就退回固定值 `keepAliveSeconds`，行为与旧版完全一致。
- 下限强制不低于 10 秒；若 `max < min` 会自动取 `min`。

Linux 生成设备码示例：

```bash
echo "web_$(cat /dev/urandom | tr -dc 'a-zA-Z0-9' | fold -w 32 | head -n 1)"
```

## 本机运行

把 `accounts.json` 放到程序目录后直接运行：

```bash
CtYun.exe
```

如果没有配置文件，也没有环境变量，程序会进入交互输入模式：

```text
账号:
密码:
继续添加账号? (y/N):
```

首次设备绑定时，程序会提示输入短信验证码。

## Docker 从源码构建（本 fork）

本 fork 的 `CtYun/Dockerfile` 已改为多阶段自构建，无需预先 `dotnet publish`。在仓库根目录直接构建：

```bash
docker build -f CtYun/Dockerfile -t ctyun:local .
```

或使用根目录的 `docker-compose.yml`：

```bash
# 首次绑定设备（需要输入短信验证码，交互模式）
docker compose run --rm ctyun

# 绑定完成后后台运行
docker compose up -d
docker compose logs -f
```

compose 默认把 `./ctyun-data` 挂到 `/app/data`，把 `accounts.json` 放进该目录即可。

## Docker 首次运行（官方镜像）

准备宿主机配置目录：

```bash
mkdir -p ./ctyun-data
```

把 `accounts.json` 放到 `./ctyun-data/accounts.json`。首次运行建议使用 `-it`，方便输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  su3817807/ctyun:latest
```

看到保活任务启动后，说明设备码已经绑定成功。之后可以按 `Ctrl+C` 停止初始化容器，再改为后台运行。

## Docker 后台运行

设备绑定完成后使用：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  su3817807/ctyun:latest
```

查看日志：

```bash
docker logs -f ctyun
```

## 兼容旧环境变量模式

单账号仍支持旧环境变量。首次绑定设备时同样使用 `-it` 输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  su3817807/ctyun:latest
```

绑定完成后改为后台运行：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  su3817807/ctyun:latest
```

建议新部署优先使用 `accounts.json`，多账号管理更清晰，也更适合 Docker 持久化。

## 日志与保活

程序会为每个账号、每台云电脑启动独立保活任务。日志格式会带上账号名和云电脑编号，便于区分：

```text
[account-a][desktop-code] -> 收到保活校验
[account-a][desktop-code] -> 发送保活响应成功
```

## 说明

登录图形验证码识别接口方案来自 [sml2h3/ddddocr](https://github.com/sml2h3/ddddocr)。
