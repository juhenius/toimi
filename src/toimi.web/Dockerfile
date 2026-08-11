# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.web/Dockerfile -t <registry>/<image>:latest .
FROM node:26.7.0-slim AS frontend
WORKDIR /app
COPY src/toimi.web/ClientApp/package.json src/toimi.web/ClientApp/package-lock.json* ./
RUN npm ci
COPY src/toimi.web/ClientApp/ .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
RUN dotnet restore src/toimi.web/toimi.web.csproj

COPY src/ src/
COPY --from=frontend /wwwroot src/toimi.web/wwwroot/
RUN dotnet publish src/toimi.web/toimi.web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.web.dll"]
