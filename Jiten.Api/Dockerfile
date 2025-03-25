FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Jiten.Core/Jiten.Core.csproj", "Jiten.Core/"]
COPY ["Jiten.Api/Jiten.Api.csproj", "Jiten.Api/"]
COPY ["Jiten.Parser/Jiten.Parser.csproj", "Jiten.Parser/"]
COPY ["Shared/appsettings.example.json", "/app/Shared/appsettings.json"]
RUN dotnet restore "Jiten.Api/Jiten.Api.csproj"
RUN dotnet build "Jiten.Api/Jiten.Api.csproj" -c Release -o /app/build
RUN dotnet publish "Jiten.Api/Jiten.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY Shared/appsettings.json /app/Shared/
ENTRYPOINT ["dotnet", "Jiten.Api.dll"]
