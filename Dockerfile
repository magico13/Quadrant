# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Quadrant.slnx ./
COPY src/Quadrant.RulesEngine/Quadrant.RulesEngine.csproj src/Quadrant.RulesEngine/
COPY src/Quadrant.Server/Quadrant.Server.csproj src/Quadrant.Server/
RUN dotnet restore src/Quadrant.Server/Quadrant.Server.csproj
COPY src/ src/
RUN dotnet publish src/Quadrant.Server/Quadrant.Server.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5180
ENV ASPNETCORE_URLS=http://+:5180
ENTRYPOINT ["dotnet", "Quadrant.Server.dll"]
