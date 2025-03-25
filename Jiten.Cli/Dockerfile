FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Jiten.Core/Jiten.Core.csproj", "Jiten.Core/"]
COPY ["Jiten.Cli/Jiten.Cli.csproj", "Jiten.Cli/"]
COPY ["Jiten.Parser/Jiten.Parser.csproj", "Jiten.Parser/"]
COPY ["Shared/appsettings.example.json", "/app/Shared/appsettings.json"]
RUN dotnet restore "Jiten.Cli/Jiten.Cli.csproj"
RUN dotnet build "Jiten.Cli/Jiten.Cli.csproj" -c Release -o /app/build
RUN dotnet publish "Jiten.Cli/Jiten.Cli.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY Shared/appsettings.json /app/Shared/
ENTRYPOINT ["dotnet", "Jiten.Cli.dll"]
