<img src="assets/icon.svg" width="64" height="64" alt="">

# social-follow-tracker

A daily report of your followers and following on social platforms. Instagram is the only one supported so far. It shows who doesn't follow you back, who you don't follow back, who unfollowed you, and every change between days. It runs on your PC in Docker (the app plus Postgres), and the report is served at <http://localhost:47321>.

## Who unfollowed you

Each run compares the new snapshot with the previous one, so something that happened yesterday is in today's report. The report opens on **History** (every change, newest first). The **Unfollowed you** tab lists just the people who stopped following you, labelled with what the data can actually show:

| You see | It means |
|---|---|
| **Unfollowed you** | They dropped off your followers list. |
| **Blocked you, or deactivated their account** | They dropped off both your lists at once: they blocked you, or deactivated or deleted their account. |

Someone who drops off only *your* following list appears in History as "gone from your following", not in this tab. Usually you unfollowed them; if you didn't, they removed you as a follower. Your own lists can't tell these apart.

The summary at the top counts the changes since you last pressed **Clear** next to it (or, before that, since the previous snapshot), so a change stays visible until you've seen it, however many runs happen in between. Clearing is remembered per browser.

Each entry shows when it happened (between which two snapshots), how long the follow had lasted, and where things stand now: *follows you again*, *you still follow them*. Renamed accounts are matched by Instagram's account id (in fetched data), so a rename shows as "renamed from @old" rather than a fake unfollow.

## Setup

Requires Docker Desktop.

```powershell
copy .env.example .env        # then edit: POSTGRES_PASSWORD, TZ, EXPORTS_DIR
docker compose up -d --build
start http://localhost:47321/settings
```

Then install the browser extension once (below). It hands your Instagram session to the app, so you never paste cookies. From then on the app fetches once a day.

### Browser extension (Brave, Chrome, Edge)

1. Open `brave://extensions` (or `chrome://extensions` / `edge://extensions`) and turn on **Developer mode**.
2. Click **Load unpacked** and pick this project's `extension` folder.
3. Stay logged in to instagram.com in that browser.

The extension sends the `sessionid` cookie to `http://localhost:47321`:
- right after install
- whenever the cookie changes (for example, you logged in again after Instagram logged you out)
- hourly, in case the app was down

The app checks a new cookie with one request to Instagram, picks up your username, and starts a fetch right away if the last one failed or none has run yet. Repeats of the same cookie cost nothing.

- **Status:** the extension's icon shows a red **!** when something's wrong. Click it for details or to **Send now**. Change the app address there if you changed `PORT`.
- **Switching accounts:** if you switch to a different Instagram account in the browser, the app refuses that cookie, so two accounts' data never get mixed. To change the tracked account on purpose, paste its cookie manually on the Settings page.
- **No extension?** The Settings page still has a "paste the cookie manually" option.

Both containers use `restart: unless-stopped`, so they come back whenever Docker Desktop starts. Turn on *Start Docker Desktop when you sign in* to make the daily run automatic. If the PC was off or asleep at the scheduled time, the run happens about a minute after Docker starts.

## How data gets in

1. **Automatic daily fetch.** At `DAILY_AT` (in the `TZ` time zone, plus up to 30 minutes of random delay), the app reads your lists through the endpoints instagram.com itself uses: one page of 50 at a time, with 2.5–6 s pauses. It stops at the first sign of trouble (expired session, checkpoint, rate limit). **Refresh** on the report (or **Fetch now** on the Settings page) runs one immediately; the report reloads when it finishes.
   > ⚠️ This is unofficial and against Instagram's Terms of Use. Running it from your own PC keeps the requests on your home IP, the same as your browser. At under 1,000 accounts it's about 20 requests a day, which is low risk but not zero: Instagram can log the session out or ask you to confirm it's you. Logging in again on instagram.com in your browser fixes it; the extension passes the new session on.
2. **Official export.** In the Instagram app go to *Accounts Center → Your information and permissions → Download your information*. Choose *Some of your information → Followers and following*, format **JSON**. Either upload the zip on the Settings page, or let it land in `EXPORTS_DIR` (mounted read-only); every `instagram-*.zip` there is imported on the next daily run. Exports include the exact date each follow happened.

A fetch is refused, and not saved, if the list comes back much shorter than the count on your profile. A truncated list would otherwise show up as a wave of fake "unfollowed you" events.

## Configuration (`.env`)

| Variable | Default | |
|---|---|---|
| `POSTGRES_PASSWORD` | (required) | Password for the bundled Postgres. Use letters and digits only. |
| `TZ` | `UTC` | IANA time zone for the schedule, e.g. `Europe/Berlin` |
| `DAILY_AT` | `09:00` | When the daily run starts |
| `PORT` | `47321` | Port on `127.0.0.1` |
| `EXPORTS_DIR` | `./imports` | Folder watched for export zips, e.g. `C:/Users/you/Downloads` |

## Security

- The page listens on `127.0.0.1` only and accepts only `localhost` as the Host name, so other machines and DNS-rebinding tricks can't reach it. Every form is CSRF-protected.
- `/api/session` only accepts requests whose `Origin` is this extension. The extension's id (`cmijdjdeopoddpijkiccfoolhbeenjoh`) is fixed by the public key in `extension/manifest.json`. Browsers always send the real Origin, so websites can't post a cookie.
- The session cookie is effectively your Instagram login. It's stored in Postgres, encrypted with ASP.NET Core Data Protection. The keys live in a separate `keys` volume, so a database dump alone doesn't reveal the cookie. If that volume is lost, paste the cookie again.
- Postgres isn't published to the host; only the app container can reach it.

## Data

Two named volumes: `pgdata` (the database) and `keys` (the encryption keys).

- Back up the database: `docker compose exec db pg_dump -U following following > backup.sql`
- Wipe everything: `docker compose down -v`

The schema is in `src/ConnectionTracker.Web/Data/Migrations` and is applied automatically on startup.

## Limitations

- Renames are only recognized between two fetched snapshots. Exports don't include account ids, so a rename across an export shows as an unfollow plus a new follow.
- An account that's deactivated only temporarily shows as "gone from both lists", then as a new follow when it comes back.
- Exports and fetches can differ slightly (deactivated accounts, for example). Mixing them can produce a few spurious events around an import.
- Full names come only from fetches; exports don't include them.

## Development

```powershell
dotnet test
dotnet tool restore; dotnet ef migrations add <Name> -p src/ConnectionTracker.Web -o Data/Migrations
```

The projects are:
- `src/ConnectionTracker.Core`: the Instagram client, export parser, diff logic, schedule math and report template (`Report/report.html`)
- `src/ConnectionTracker.Web`: the ASP.NET Core app (Postgres via EF Core, daily scheduler, Settings page)
- `tests/ConnectionTracker.Tests`

## License

[MIT](LICENSE)
