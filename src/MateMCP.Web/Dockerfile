FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/MateMCP.Web/MateMCP.Web.csproj
RUN dotnet publish src/MateMCP.Web/MateMCP.Web.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8082
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8082
ENTRYPOINT ["dotnet", "MateMCP.Web.dll"]
