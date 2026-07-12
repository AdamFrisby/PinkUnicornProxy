FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/PinkUnicornProxy/PinkUnicornProxy.csproj src/PinkUnicornProxy/
COPY src/PinkUnicornProxy/packages.lock.json src/PinkUnicornProxy/
RUN dotnet restore src/PinkUnicornProxy/PinkUnicornProxy.csproj --locked-mode

COPY src/PinkUnicornProxy/ src/PinkUnicornProxy/
RUN dotnet publish src/PinkUnicornProxy/PinkUnicornProxy.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PinkUnicornProxy.dll"]
