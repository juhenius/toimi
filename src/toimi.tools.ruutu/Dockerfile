# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.tools.ruutu/Dockerfile -t <registry>/<image>:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.ruutu/toimi.tools.ruutu.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.ruutu/toimi.tools.ruutu.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.ruutu.dll"]
