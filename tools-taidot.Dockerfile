FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.tools.ajastin/toimi.tools.ajastin.csproj src/toimi.tools.ajastin/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.muistio/toimi.tools.muistio.csproj src/toimi.tools.muistio/
COPY src/toimi.tools.muistutin/toimi.tools.muistutin.csproj src/toimi.tools.muistutin/
COPY src/toimi.tools.taidot/toimi.tools.taidot.csproj src/toimi.tools.taidot/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.taidot/toimi.tools.taidot.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.taidot/toimi.tools.taidot.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.taidot.dll"]
