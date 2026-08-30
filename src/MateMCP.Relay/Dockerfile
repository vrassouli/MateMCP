# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/MateMCP.Relay/MateMCP.Relay.csproj", "src/MateMCP.Relay/"]
RUN dotnet restore "src/MateMCP.Relay/MateMCP.Relay.csproj"

COPY . .
RUN dotnet publish "src/MateMCP.Relay/MateMCP.Relay.csproj" \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "MateMCP.Relay.dll"]
