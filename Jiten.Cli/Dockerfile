FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Jiten.Core/Jiten.Core.csproj", "Jiten.Core/"]
COPY ["Jiten.Cli/Jiten.Cli.csproj", "Jiten.Cli/"]
COPY ["Jiten.Parser/Jiten.Parser.csproj", "Jiten.Parser/"]

# Create the directory structure first
RUN mkdir -p /src/Shared/

# Copy the example file to the correct location
COPY ["Shared/sharedsettings.example.json", "/src/Shared/sharedsettings.json"]
COPY ["Shared/resources", "/src/Shared/resources"]

RUN dotnet restore "Jiten.Cli/Jiten.Cli.csproj"
COPY . .
RUN dotnet build "Jiten.Cli/Jiten.Cli.csproj" -c Release -o /app/build
RUN dotnet publish "Jiten.Cli/Jiten.Cli.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Ensure the Shared directory exists in the final image
RUN mkdir -p /app/Shared
# Copy the settings file from the build stage
COPY --from=build /src/Shared/sharedsettings.json /app/Shared/
COPY --from=build /src/Shared/resources /app/resources
ENTRYPOINT ["dotnet", "Jiten.Cli.dll"]
