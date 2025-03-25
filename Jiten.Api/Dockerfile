FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Jiten.Core/Jiten.Core.csproj", "Jiten.Core/"]
COPY ["Jiten.Api/Jiten.Api.csproj", "Jiten.Api/"]
COPY ["Jiten.Parser/Jiten.Parser.csproj", "Jiten.Parser/"]

# Create the directory structure first
RUN mkdir -p /src/Shared/

# Copy the example file to the correct location
COPY ["Shared/sharedsettings.example.json", "/src/Shared/sharedsettings.json"]

RUN dotnet restore "Jiten.Api/Jiten.Api.csproj"
COPY . .
RUN dotnet build "Jiten.Api/Jiten.Api.csproj" -c Release -o /app/build
RUN dotnet publish "Jiten.Api/Jiten.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Ensure the Shared directory exists in the final image
RUN mkdir -p /app/Shared
# Copy the settings file from the build stage
COPY --from=build /src/Shared/sharedsettings.json /app/Shared/
ENTRYPOINT ["dotnet", "Jiten.Api.dll"]