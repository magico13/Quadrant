# Quadrant
A Starfinder Inspired Space Combat Game

## Run with Docker

Prebuilt container images are published to [GitHub Container Registry](https://github.com/magico13/Quadrant/pkgs/container/quadrant):

```sh
docker pull ghcr.io/magico13/quadrant:latest
docker run --name quadrant-server -p 5180:5180 -v quadrant-data:/app/data --restart unless-stopped ghcr.io/magico13/quadrant:latest
```

Then open <http://localhost:5180>. The named volume keeps saved games when the container is replaced. For local development, `docker-compose.override.yml` is applied automatically by `docker compose up`; it builds from source and mounts `wwwroot` so UI edits appear on refresh without restarting. To run the published image locally without the development override, use `docker compose -f docker-compose.yml up -d`.

Game sessions are saved in SQLite. Docker Compose stores the database in the
`quadrant-data` named volume, which survives container rebuilds and `docker compose down`.
Avoid `docker compose down -v` if you want to keep saved games. Outside Docker, the
default database is `src/Quadrant.Server/data/quadrant.db`; set `QUADRANT_DB_PATH`
to choose a different file.

When a new game is created, completed games, games left in Setup for five minutes,
and games with no player action for 30 days are removed. Viewing a game does not
extend its lifetime.

## Paizo attribution

Pathfinder and Starfinder are trademarks of Paizo Inc. This project uses trademarks and/or copyrights owned by Paizo Inc., used under the [Paizo Community Use Policy](https://paizo.com/licenses/communityuse). This project is not published, endorsed, or specifically approved by Paizo. For more information about Paizo Inc. and Paizo products, visit [paizo.com](https://paizo.com).

This project is free to use and does not charge for access to Paizo-related content. Starfinder rules and setting material remain the property of their respective rights holders; this project's MIT license applies only to its original code and content.
