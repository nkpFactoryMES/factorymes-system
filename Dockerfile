FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY .packages/ ./.packages/
COPY tools/FactoryMES.Tooling/FactoryMES.Tooling.csproj tools/FactoryMES.Tooling/packages.lock.json ./tools/FactoryMES.Tooling/
RUN dotnet restore tools/FactoryMES.Tooling/FactoryMES.Tooling.csproj --locked-mode
COPY tools/FactoryMES.Tooling/ ./tools/FactoryMES.Tooling/
RUN dotnet publish tools/FactoryMES.Tooling/FactoryMES.Tooling.csproj -c Release --no-restore -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app
ENTRYPOINT ["dotnet", "FactoryMES.Tooling.dll"]
