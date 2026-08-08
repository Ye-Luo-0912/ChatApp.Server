# 构建上下文为 ChatApp.Server 仓库根；版本化本地包使构建不依赖兄弟源码仓库。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ChatApp.Server.csproj ./
COPY Core/Core.csproj Core/
COPY Infrastructure/Infrastructure.csproj Infrastructure/
COPY Directory.Build.props Directory.Packages.props NuGet.Config packages.lock.json ./
COPY Core/packages.lock.json Core/
COPY Infrastructure/packages.lock.json Infrastructure/
COPY packages/ packages/
RUN dotnet restore ChatApp.Server.csproj --locked-mode
COPY . .
RUN dotnet publish ChatApp.Server.csproj \
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
