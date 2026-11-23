# Home features in this fork (zmakis/openjibo-fork)

Extra features running on a home Jibo with a self-hosted OpenJibo server and Claude as
the brain. Everything is opt-in or has a switch in `.env`. See `.env.example` for all settings.

## Claude brain
- One Claude Messages API call per question. Claude can answer directly (with a mood),
  ask Jibo to run one of his own skills (the `jibo_command` tool), or search the web for
  current events (`OPENJIBO_WEB_SEARCH`, `OPENJIBO_WEB_SEARCH_MAX_USES`).
- Answers come back without "According to ..." prefixes.

## Expressions
- Answers can carry a mood (happy / sad) plus a jiboji, emoji or gesture.
- Emoji are eye-only, so the spoken words are wrapped inside the `<anim>` tag; that keeps
  the picture on screen while Jibo talks.

## Live camera vision ("what do you see")
- See-questions take a fresh photo from Jibo's own camera through the robot's local media
  service and send it to the AI. Set `OPENJIBO_JIBO_CAMERA_URL=http://<jibo-ip>:8486`.
- The camera needs about 2 minutes to be ready after a robot or server restart.

## Camera light (base light ring)
- While Jibo describes a photo he just took (or learns a face), his base light ring glows
  white, like a recording light. It uses the yoga skill's LED-only animation:
  `<anim cat='yoga' filter='light-ring-only, breathing' layers='LED' nonBlocking='true' />`.
- The glow plays while he answers, not at the exact moment of the photo.
- Turn off with `OPENJIBO_LIGHT_RING_ON_PHOTO=false`.

## Face memory (server side, local)
- The `faces` container (OpenCV YuNet + SFace, no cloud) learns family faces. Face data stays
  in the `faces-data` volume.
- Teach someone: say "learn Makis face", or run
  `docker compose --profile self-hosted exec faces python /app/teach.py Makis` 3-5 times,
  turning the head slightly each time. `--list` and `--forget NAME` also work.
- "Who am I" / "what do you see" then use the names.
- "I'm Makis" phrases are caught on the robot and never reach the server, so use
  "learn X face" instead.
- Strictness: `OPENJIBO_FACES_MATCH_THRESHOLD` (default 0.36). `docker compose logs faces`
  shows the match scores.

## Jibo's own face/voice ID in AI answers
- With each turn the robot sends `context.runtime.perception` (`speaker`, `peoplePresent`).
  OpenJibo maps those IDs to loop first names, and the name is now passed to the AI.
- When a fresh photo's face memory names someone different, the AI trusts the photo.
- Check it: `docker compose logs -f api | grep "Robot perception"`.

## Home location override
- If Jibo reports the wrong city or time zone (for example a robot first set up abroad), set
  `OPENJIBO_HOME_LATITUDE`, `OPENJIBO_HOME_LONGITUDE`, `OPENJIBO_HOME_LOCATION_NAME` and
  `OPENJIBO_HOME_TIMEZONE` in `.env`. Home weather, greetings, stats days and time answers from the
  server then use them. Jibo's own on-screen clock still comes from the robot.

## Morning hello
- The first time each day Jibo greets someone he recognises (his own proactive greeting),
  he also says today's home weather and a fun fact. Later greetings stay short.
- Off switch: `OPENJIBO_MORNING_HELLO=false`. Log line: `Morning hello: added ...`.

## House report (Home Assistant)
- "Hey Jibo, house report", "how's the house?", "which lights are on?": Jibo reads Home Assistant
  and says which lights are on, and shows them as text on his face. Read-only.
- Set `OPENJIBO_HA_URL` (e.g. `http://<ha-ip>:8123`) and `OPENJIBO_HA_TOKEN` (a long-lived access
  token from your Home Assistant profile) in `.env`. Without them the phrases go to the AI as usual.

## Daily stats ("how was your day?")
- Say "Hey Jibo, how was your day?", "show me your stats", "how many conversations did you have"
  or "how was your week". Jibo says the numbers and shows them as text on his face.
- Counted per day (the robot's local day): conversations, answers from the AI, skills the AI
  picked, talks handled without the AI (jokes, timers, weather...), camera photos, faces
  recognised, who he talked with most and his busiest hour.
- Kept in `/data/jibo-stats.json` on the API data volume (about a year). Off switch:
  `OPENJIBO_STATS=false`.
- Sleep time and sounds heard happen inside the robot and aren't visible to the server yet.

## Speech recognition (Whisper)
- With `OPENJIBO_STT_WHISPER_LANGUAGE=auto`, short English was sometimes detected as Korean,
  Spanish or Polish. Now, if the detected language isn't in
  `OPENJIBO_STT_WHISPER_ALLOWED_LANGUAGES` (default `en,el`), the same recording is re-read
  as English. Set `any` to keep every detected language.

## Server reliability
- `scripts/self-hosted/home-server-watchdog.sh` (installed by `install-watchdog.sh`) cleans
  disk space at 85% and restarts the HTTPS proxy if needed.
- Keep the Docker build cache: a rebuild takes 2-3 minutes with it and 10-15 without it.

## Upstream
- Synced with upstream OpenJibo (`git fetch upstream && git merge upstream/main`).
- Contributed upstream: PR #341 (weather, watchdog, Whisper profile; merged) and PR #342
  (live camera vision, opt-in).
