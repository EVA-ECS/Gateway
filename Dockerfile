FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Contracts/EVA-ECS.Chat.Contracts/EVA-ECS.Chat.Contracts.csproj Contracts/EVA-ECS.Chat.Contracts/
COPY Gateway/Gateway.csproj Gateway/
RUN dotnet restore Gateway/Gateway.csproj --source https://api.nuget.org/v3/index.json

COPY Contracts/EVA-ECS.Chat.Contracts/ Contracts/EVA-ECS.Chat.Contracts/
COPY Gateway/ Gateway/
RUN dotnet publish Gateway/Gateway.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Gateway.dll"]
