# Arrivals

A compact, self-hosted release dashboard for a Yamtrack + Sonarr + Radarr +
Jellyfin stack. It recreates the useful density of the MyEpisodes private show
list while adding movies, acquisition state and direct links to each service.

## What the first version does

- Shows Yamtrack, Sonarr and Radarr releases in one chronological list.
- Defaults to the previous 30 days and next 90 days.
- Hides watched entries by default, with a filter to include them.
- Gets episode watched state in batches of one Yamtrack API request per season.
- Matches movies by TMDB ID.
- Matches episodes by TMDB series ID + season number + episode number.
- Shows Sonarr/Radarr file state as **Acquired**.
- Optionally checks whether the item exists in Jellyfin.
- Can mark a movie watched in Yamtrack.
- Links directly to Yamtrack, Sonarr, Radarr and Jellyfin.

## Deliberate limitation: marking episodes watched

The episode checkbox is disabled in this version. The current Yamtrack API
branch has read endpoints for episode state, but its generic provider-backed
`POST /api/v1/media/episode/` path does not create the required parent season or
correctly use `episode_number`. See
[`docs/Yamtrack-episode-write-gap.md`](docs/Yamtrack-episode-write-gap.md).

Movie writes use the supported endpoint:

```http
POST /api/v1/media/movie/
```

with `status: 3` and the current watch time.

## Run with Docker

1. Copy the environment file:

   ```bash
   cp .env.example .env
   ```

2. Add the API keys.
3. Edit `docker-compose.example.yml` so the service URLs and external Docker
   network match your installation. Set each `PublicBaseUrl` to the URL your browser can reach if the API uses internal Docker hostnames.
4. Start it:

   ```bash
   docker compose -f docker-compose.example.yml up -d --build
   ```

The example binds to `127.0.0.1:8095`. Publish it through Caddy or access it over
Tailscale rather than exposing it directly to the internet. The dashboard has no
login system of its own; all upstream credentials remain server-side.

## Run with the .NET SDK

The project targets .NET 10 and has no third-party NuGet dependencies:

```bash
dotnet run --project src/Arrivals/Arrivals.csproj
```

Use user secrets for local development:

```bash
dotnet user-secrets set Yamtrack:BaseUrl "http://yamtrack.lan/" \
  --project src/Arrivals/Arrivals.csproj
dotnet user-secrets set Yamtrack:ApiKey "..." \
  --project src/Arrivals/Arrivals.csproj
```

Environment variables use the normal ASP.NET Core double-underscore notation,
for example `Yamtrack__ApiKey`.

## Configuration

| Setting | Purpose |
|---|---|
| `Dashboard:PastDays` | Default lookback, 30 |
| `Dashboard:FutureDays` | Default lookahead, 90 |
| `Dashboard:CacheMinutes` | In-memory API cache lifetime |
| `Dashboard:TimeZoneId` | Display and date-filter timezone |
| `Dashboard:MovieDatePreference` | `DigitalThenCinema`, `CinemaThenDigital`, or `Earliest` |
| `Dashboard:IncludeUnmonitored` | Include unmonitored Sonarr/Radarr entries |
| `Dashboard:EnableMovieMarkWatched` | Enables the movie write button |
| `Yamtrack:BaseUrl` / `ApiKey` | Required; target the `:api` image |
| `*:PublicBaseUrl` | Optional browser-facing URL when the API URL uses an internal Docker hostname |
| `Sonarr:BaseUrl` / `ApiKey` | Optional but needed for TV acquisition state |
| `Radarr:BaseUrl` / `ApiKey` | Optional but needed for movie acquisition state |
| `Jellyfin:Enabled` | Enables Jellyfin lookup |
| `Jellyfin:BaseUrl` / `ApiKey` / `UserId` | Required when Jellyfin lookup is enabled |

## Matching and source ownership

```text
Yamtrack calendar  ─┐
Sonarr calendar     ├─> canonical movie/episode keys ─> one release list
Radarr calendar     ┘

movie key   = movie:tmdb:{tmdbId}
episode key = episode:tmdb:{seriesTmdbId}:S{season}:E{episode}
```

- **Watched** comes from Yamtrack.
- **Acquired** comes from Sonarr/Radarr `hasFile`/file IDs.
- **In Jellyfin** is a separate optional check.
- Release rows are the union of all configured calendars, so an item can appear
  even if only Sonarr or Radarr currently knows about it.

For known numbering disagreements, do not add fuzzy matching silently. Add an
explicit mapping layer later so MythBusters-style cases remain inspectable.

## Useful Yamtrack smoke tests

```bash
curl -H "X-API-Key: $YAMTRACK_API_KEY" \
  http://yamtrack.lan/api/v1/info/

curl -H "X-API-Key: $YAMTRACK_API_KEY" \
  "http://yamtrack.lan/api/v1/calendar/?start_date=2026-07-01&end_date=2026-08-31&limit=5"
```

Swagger should remain available from the API image at `/api/docs/`.

## Next practical steps

1. Run against the cloned Yamtrack data and inspect unmatched rows.
2. Enable Jellyfin only after the core view is stable; it adds per-show library
   queries and is not required for the acquired indicator.
3. Add a small explicit override store for rare Sonarr/TMDB numbering conflicts.
4. Add the Yamtrack episode history POST endpoint, then enable episode writes.
5. Add authentication in front of Arrivals if it is reachable beyond the LAN.
