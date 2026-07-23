# Gemini Build Brief — Whose IPTV Android (From Scratch)

## 1) How to use this brief

1. Open Android Studio.
2. Open Gemini.
3. Paste the **Master Prompt** section below.
4. Ask Gemini to execute phase by phase and wait for code review after each phase.

---

## 2) Master Prompt (paste into Gemini)

You are a senior Android engineer. Build a production-grade Android app from scratch called **Whose IPTV** with the same purpose and user flows as the existing Windows app.

### Product goal
Create a premium IPTV player Android app with:
- source import (Xtream, M3U URL, M3U file, direct M3U8),
- fast browsing of categories/channels,
- in-app playback,
- favorites and recents,
- session restore,
- robust error handling and logging.

### Required tech stack
- **Language:** Kotlin
- **UI:** Jetpack Compose (Material 3)
- **Architecture:** Clean Architecture + MVVM + Repository pattern
- **DI:** Hilt
- **Networking:** Retrofit + OkHttp + Kotlinx Serialization (or Moshi)
- **Local storage:** Room
- **Settings/state:** DataStore
- **Player:** ExoPlayer (Media3)
- **Async:** Coroutines + Flow
- **Image loading:** Coil
- **Testing:** JUnit + Turbine + MockK + Compose UI tests

### App modules (must create)
- `app` (Compose, navigation, DI composition)
- `core:model` (domain models)
- `core:common` (utilities, result wrapper, dispatchers)
- `data:network` (Retrofit APIs, DTOs, mappers)
- `data:local` (Room entities/dao, DataStore)
- `domain` (use cases + repository interfaces)
- `feature:sources`
- `feature:catalog`
- `feature:player`
- `feature:settings`

### Feature parity requirements

#### A. Source management
- Add source via:
  - Xtream credentials (server URL, username, password)
  - M3U URL
  - M3U file picker
  - direct M3U8 URL
- Validate inputs before import.
- Persist sources.
- Allow source deletion.
- Show source status fields:
  - account state
  - expiration datetime when available
  - derived days remaining

#### B. Catalog browsing
- Show categories for selected source.
- Show channels for selected category.
- Category and channel search.
- Channel item includes:
  - name
  - logo
  - optional current program text
  - favorite toggle
- Keep lists performant for large playlists.

#### C. Playback
- Embedded ExoPlayer surface in app.
- Commands:
  - play selected channel
  - stop
  - mute/unmute
- Show status text:
  - idle / connecting / buffering / playing / stopped / failed
- Graceful playback failure handling with user-friendly messages.

#### D. Session persistence
- Persist and restore:
  - last source
  - last category
  - last channel
  - favorites
  - recents (limit to 12)
  - mute state

#### E. UX and responsiveness
- Dark premium theme.
- Responsive layouts for phone and tablet.
- Loading, empty, and error states for each section.
- Keep UI smooth during network and parsing tasks.

### Data contracts and behaviors

Implement equivalent core models and flows:
- `PlaylistSource(id, name, kind, endpoint, statusInfo)`
- `Category(id, name, sortOrder)`
- `Channel(id, categoryId, name, streamUri, logoUri, currentProgram, isFavorite)`
- `UserSessionState(lastSourceId, lastCategoryId, lastChannelId, favoriteIds, recentIds, isMuted)`

Supported source kinds:
- Xtream
- M3U URL
- M3U file
- M3U8 link

Xtream behavior:
- Authenticate and load categories + streams.
- Parse account state and expiration timestamp when provided.

M3U behavior:
- Parse EXTINF and group-title reliably.
- Build categories and channels.
- Skip invalid stream URIs safely.

### Reliability requirements
- Never crash from malformed source data.
- Wrap all import/playback operations in typed result states.
- Add timeout/retry strategy for transient network errors.
- Emit structured logs for import/playback failures.

### Security/privacy baseline
- Do not log credentials.
- Store sensitive fields safely.
- Validate/sanitize imported input.

### Delivery plan (must follow)

#### Phase 1 — Foundation
- Create project + modules + buildSrc/version catalog.
- Configure Hilt, Compose, navigation, CI build task.

#### Phase 2 — Core models and persistence
- Add domain models, Room schema, DataStore session state.

#### Phase 3 — Source import
- Implement Xtream and M3U import pipelines.
- Source list UI + validation + delete.

#### Phase 4 — Catalog UI
- Category/channel lists with search + favorite toggles.

#### Phase 5 — Player
- ExoPlayer integration + playback state mapping.
- Add play/stop/mute and error handling.

#### Phase 6 — Session restore and recents
- Wire persistence and startup restore behavior.

#### Phase 7 — Polish and tests
- Responsiveness, loading/empty/error states.
- Unit tests for parsing/use cases + UI tests for key flows.

### Definition of done
- App installs and runs reliably on modern Android devices.
- All source types import successfully.
- Category/channel browsing and search work at scale.
- In-app playback works with status updates and graceful failures.
- Favorites/recents/session restore function correctly.
- No unhandled crashes in common user flows.

### Output format from Gemini
For each phase, return:
1. File tree changes
2. Full code for new/updated files
3. Build/run commands
4. Test checklist
5. Known risks and next phase plan

---

## 3) Optional follow-up prompt for Gemini

After Gemini delivers Phase 1, use this follow-up:

"Continue with Phase 2 now. Keep previous architecture unchanged. Produce complete code changes only, with no pseudocode."

