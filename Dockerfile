# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
ARG GIT_COMMIT=unknown
WORKDIR /src
COPY Quadrant.slnx ./
COPY src/Quadrant.RulesEngine/Quadrant.RulesEngine.csproj src/Quadrant.RulesEngine/
COPY src/Quadrant.Server/Quadrant.Server.csproj src/Quadrant.Server/
RUN dotnet restore src/Quadrant.Server/Quadrant.Server.csproj
COPY src/ src/
RUN sed -i "s/const CLIENT_BUILD_ID = 'development';/const CLIENT_BUILD_ID = '${GIT_COMMIT}';/" src/Quadrant.Server/wwwroot/js/game.js \
    && dotnet publish src/Quadrant.Server/Quadrant.Server.csproj -c Release -o /app/publish \
        -p:InformationalVersion=${GIT_COMMIT} \
        -p:IncludeSourceRevisionInInformationalVersion=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5180
ENV ASPNETCORE_URLS=http://+:5180
ENTRYPOINT ["dotnet", "Quadrant.Server.dll"]
