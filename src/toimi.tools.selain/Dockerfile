# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.tools.selain/Dockerfile -t <registry>/<image>:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
COPY src/toimi.tools.selain/toimi.tools.selain.csproj src/toimi.tools.selain/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.selain/toimi.tools.selain.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.selain/toimi.tools.selain.csproj -c Release -o /app

# Runtime: Playwright's image ships Chromium + every OS dep at /ms-playwright.
# The tag MUST match the Microsoft.Playwright package version.
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble
WORKDIR /app
COPY --from=build /app .
# The NuGet-extracted Playwright driver ships owner-only exec (rwxrw-r--);
# in the pod the files are root-owned but run as UID 1654, so "other" needs
# the exec bit too. a+rX marks only already-executable files and dirs.
RUN chmod -R a+rX /app/.playwright
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.selain.dll"]
