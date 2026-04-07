# Stage 1 – Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY SiteCompare/SiteCompare.csproj SiteCompare/
RUN dotnet restore SiteCompare/SiteCompare.csproj

COPY SiteCompare/ SiteCompare/
RUN dotnet publish SiteCompare/SiteCompare.csproj -c Release -o /app/publish --no-restore

# Stage 2 – Runtime (includes Chromium + all Playwright dependencies)
FROM mcr.microsoft.com/playwright/dotnet:v1.58.0-noble AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "SiteCompare.dll"]
