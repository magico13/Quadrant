#!/usr/bin/env bash
set -euo pipefail

# Run the published image without the automatically loaded development override.
repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
compose=(docker compose --project-directory "$repo_dir" -f "$repo_dir/docker-compose.yml")
image=ghcr.io/magico13/quadrant:latest

# A slow pull must not overlap the next cron invocation.
exec 9>"${TMPDIR:-/tmp}/quadrant-image-update-$(id -u).lock"
flock -n 9 || exit 0

container_id=$("${compose[@]}" ps -q --all server)
if [[ -z "$container_id" ]]; then
    echo "Quadrant Compose service is not deployed; start it before enabling updates." >&2
    exit 1
fi
running_image=$(docker inspect --format '{{.Image}}' "$container_id")
docker pull "$image" >/dev/null
latest_image=$(docker image inspect --format '{{.Id}}' "$image")

if [[ "$running_image" != "$latest_image" ]]; then
    echo "$(date -Is) Updating Quadrant to $latest_image"
    "${compose[@]}" up -d --no-build --no-deps server
fi
