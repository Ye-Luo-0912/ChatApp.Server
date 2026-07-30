# 构建上下文为 CHAT 仓库根（含 ChatApp.Server 与 ChatApp.RealtimeServices）
# docker compose build 使用 context: ..
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ChatApp.Server/ChatApp.Server.csproj ChatApp.Server/
COPY ChatApp.Server/Core/Core.csproj ChatApp.Server/Core/
COPY ChatApp.Server/Infrastructure/Infrastructure.csproj ChatApp.Server/Infrastructure/
COPY ChatApp.Server/Directory.Build.props ChatApp.Server/
COPY ChatApp.RealtimeServices/ChatApp.Realtime.Abstractions/ChatApp.Realtime.Abstractions.csproj ChatApp.RealtimeServices/ChatApp.Realtime.Abstractions/
COPY ChatApp.RealtimeServices/ChatApp.Realtime.Integration/ChatApp.Realtime.Integration.csproj ChatApp.RealtimeServices/ChatApp.Realtime.Integration/
RUN dotnet restore ChatApp.Server/ChatApp.Server.csproj
COPY ChatApp.Server/ ChatApp.Server/
COPY ChatApp.RealtimeServices/ ChatApp.RealtimeServices/
RUN dotnet publish ChatApp.Server/ChatApp.Server.csproj \
    -c Release -o /app/publish --no-restore -m:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV EnableHttpsRedirection=false
ENV AvatarStorage__LocalRootPath=/app/App_Data/avatars
ENV AttachmentStorage__LocalRootPath=/app/App_Data/attachments
ENV DataExport__LocalRootPath=/app/App_Data/exports
EXPOSE 8080

USER root
RUN groupadd --system --gid 10001 chatapp \
 && useradd --system --uid 10001 --gid chatapp --home-dir /app --shell /usr/sbin/nologin chatapp \
 && mkdir -p /app/App_Data/avatars /app/App_Data/attachments /app/App_Data/exports \
 && chown -R chatapp:chatapp /app

COPY --from=build --chown=chatapp:chatapp /app/publish .
USER chatapp
ENTRYPOINT ["dotnet", "ChatApp.Server.dll"]
