FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ARG GITHUB_USER
ARG GITHUB_TOKEN

ENV GITHUB_USER=$GITHUB_USER
ENV GITHUB_TOKEN=$GITHUB_TOKEN

COPY nuget.config ./
COPY *.csproj ./

RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Gateway.dll"]