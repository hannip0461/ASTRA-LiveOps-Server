FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS source
WORKDIR /src
COPY . .
RUN dotnet restore Astra.LiveOps.slnx

FROM source AS publish-api
RUN dotnet publish src/Astra.Api/Astra.Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM source AS publish-admin
RUN dotnet publish src/Astra.Admin/Astra.Admin.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM source AS publish-silo
RUN dotnet publish src/Astra.Silo/Astra.Silo.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM source AS publish-tcp-gateway
RUN dotnet publish src/Astra.TcpGateway/Astra.TcpGateway.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM source AS publish-worker
RUN dotnet publish src/Astra.Worker/Astra.Worker.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS api
WORKDIR /app
COPY --from=publish-api /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Astra.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS admin
WORKDIR /app
COPY --from=publish-admin /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Astra.Admin.dll"]

# Astra.Infrastructure가 사용하는 Microsoft.AspNetCore.App 공유 프레임워크를 포함한다.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS silo
WORKDIR /app
COPY --from=publish-silo /app/publish .
EXPOSE 11111 30000
USER $APP_UID
ENTRYPOINT ["dotnet", "Astra.Silo.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS tcp-gateway
WORKDIR /app
COPY --from=publish-tcp-gateway /app/publish .
EXPOSE 5300
USER $APP_UID
ENTRYPOINT ["dotnet", "Astra.TcpGateway.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS worker
WORKDIR /app
COPY --from=publish-worker /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Astra.Worker.dll"]
