# Quadrant
A Starfinder Inspired Space Combat Game

Game sessions are saved in SQLite. Docker Compose stores the database in the
`quadrant-data` named volume, which survives container rebuilds and `docker compose down`.
Avoid `docker compose down -v` if you want to keep saved games. Outside Docker, the
default database is `src/Quadrant.Server/data/quadrant.db`; set `QUADRANT_DB_PATH`
to choose a different file.

When a new game is created, completed games, games left in Setup for five minutes,
and games with no player action for 30 days are removed. Viewing a game does not
extend its lifetime.
