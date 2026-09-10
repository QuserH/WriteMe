FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY native/Directory.Build.props native/Directory.Build.props
COPY native/WriteMe.Core/WriteMe.Core.csproj native/WriteMe.Core/packages.lock.json native/WriteMe.Core/
COPY native/WriteMe.SyncServer/WriteMe.SyncServer.csproj native/WriteMe.SyncServer/packages.lock.json native/WriteMe.SyncServer/
RUN dotnet restore native/WriteMe.SyncServer/WriteMe.SyncServer.csproj --locked-mode
COPY native/WriteMe.Core/ native/WriteMe.Core/
COPY native/WriteMe.SyncServer/ native/WriteMe.SyncServer/
RUN dotnet publish native/WriteMe.SyncServer/WriteMe.SyncServer.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 WRITEME_SYNC_DATA=/data
RUN mkdir /data && chown app:app /data
USER app
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "WriteME.SyncServer.dll"]
