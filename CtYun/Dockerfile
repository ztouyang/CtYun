# ---- 构建阶段:在容器内还原并发布，无需本地预先 publish ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先拷贝项目文件以利用分层缓存
COPY CtYun/CtYun.csproj CtYun/
RUN dotnet restore CtYun/CtYun.csproj

# 拷贝其余源码并发布（框架依赖方式，关闭 AOT，产物为 CtYun.dll）
COPY CtYun/ CtYun/
RUN dotnet publish CtYun/CtYun.csproj -c Release -o /app/publish -p:PublishAot=false

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

COPY --from=build /app/publish/ ./

ENV CTYUN_DATA_DIR=/app/data

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "CtYun.dll"]
