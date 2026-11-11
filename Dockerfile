# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY Quadrant.slnx ./
COPY src/Quadrant.RulesEngine/Quadrant.RulesEngine.csproj src/Quadrant.RulesEngine/
COPY src/Quadrant.Server/Quadrant.Server.csproj src/Quadrant.Server/
RUN dotnet restore src/Quadrant.Server/Quadrant.Server.csproj
COPY src/ src/
RUN dotnet publish src/Quadrant.Server/Quadrant.Server.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5180
ENV ASPNETCORE_URLS=http://+:5180
ARG GIT_COMMIT=unknown
ENV QUADRANT_BUILD_COMMIT=${GIT_COMMIT}
ENTRYPOINT ["dotnet", "Quadrant.Server.dll"]
