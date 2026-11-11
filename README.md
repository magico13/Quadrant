# Quadrant
A Starfinder Inspired Space Combat Game

## Run with Docker

Prebuilt container images are published to [GitHub Container Registry](https://github.com/magico13/Quadrant/pkgs/container/quadrant):

```sh
docker pull ghcr.io/magico13/quadrant:latest
docker run --name quadrant-server -p 5180:5180 -v quadrant-data:/app/data --restart unless-stopped ghcr.io/magico13/quadrant:latest
```

Then open <http://localhost:5180>. The named volume keeps saved games when the container is replaced. For local development, `docker-compose.override.yml` is applied automatically by `docker compose up`; it builds from source and mounts `wwwroot` so UI edits appear on refresh without restarting. To run the published image locally without the development override, use `docker compose -f docker-compose.yml up -d`.

### Automatic image updates

For a deployment using the published Compose image, `scripts/update-image.sh` pulls the latest image from GHCR and recreates the server only when its image has changed. It preserves the named data volume. It deliberately ignores the development override; do not use it for a source-built development server. The cron user needs Docker access and, if the image is private, an existing `docker login ghcr.io` session.

The client build's short identifier is shown beside the app name; hover over it to see the full client and server build IDs. `/api/health` returns the server's full `build` ID. The client compares its embedded build ID with the server once a minute and offers a refresh when they differ. GitHub Actions embeds the full commit SHA in both the client and server. Local source builds use `development`; set `GIT_COMMIT` when building if you want another ID, such as `GIT_COMMIT=$(git rev-parse HEAD) docker compose up --build`.

On the Docker LXC at `192.168.68.94`, copy the update script from this checkout (run from the repository directory on your local machine):

```sh
ssh root@192.168.68.94 'mkdir -p /root/containers/quadrant/scripts'
scp scripts/update-image.sh root@192.168.68.94:/root/containers/quadrant/scripts/update-image.sh
```

The script uses `/root/containers/quadrant/docker-compose.yml`; ensure that file defines the published `ghcr.io/magico13/quadrant:latest` image as the `server` service. On the LXC, start that Compose deployment if it is not already running, then add this line to root's crontab with `crontab -e`:

```cron
* * * * * /bin/bash /root/containers/quadrant/scripts/update-image.sh >> /root/containers/quadrant/image-update.log 2>&1
```

The job checks once per minute. Start the published Compose deployment first from `/root/containers/quadrant` with `docker compose -f docker-compose.yml up -d`; the script does not create a new deployment. Image updates recreate the container and briefly interrupt active games.

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
