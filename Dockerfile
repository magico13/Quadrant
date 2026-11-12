# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5180
ENV ASPNETCORE_URLS=http://+:5180
ENTRYPOINT ["dotnet", "Quadrant.Server.dll"]
