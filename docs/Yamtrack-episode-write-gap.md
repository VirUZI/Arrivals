# Yamtrack episode write gap

The current `feat/add-api` branch can **read** episode state efficiently through:

```http
GET /api/v1/media/tv/{source}/{media_id}/{season}/episodes/
```

It cannot safely create a single episode watch through the public API yet.
The generic endpoint accepts `episode` as a media type, but its provider-backed
creation path does not resolve `episode_number` and the required parent `Season`
instance before constructing the `Episode` model.

The clean API addition would be:

```http
POST /api/v1/media/tv/tmdb/{show_id}/{season}/{episode}/history/
Content-Type: application/json

{
  "end_date": "2026-07-29T14:00:00+02:00"
}
```

Expected behavior:

1. Validate that the TMDB episode exists.
2. Get or create the tracked TV and season records for the authenticated user.
3. Create one `Episode` consumption with the supplied watch date.
4. Return the created consumption, ideally with HTTP 201.
5. Accept an optional idempotency key so a network retry cannot create a rewatch.

Once this exists, implement `YamtrackClient.MarkEpisodeWatchedAsync` and enable
its checkbox in `Pages/Index.cshtml`.
