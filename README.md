<img src="assets/icon.svg" width="64" height="64" alt="">

# following

A daily report of your Instagram followers and following. It shows who doesn't follow you back, who you don't follow back, who unfollowed you, and every change between days. It runs on your PC in Docker (the app plus Postgres), and the report is served at <http://localhost:47321>.

## Who unfollowed you

Each run compares the new snapshot with the previous one, so something that happened yesterday is in today's report. The report opens on **History** (every change, newest first). The **Unfollowed you** tab lists just the losses and labels each case with what the data can actually show:

| You see | It means |
|---|---|
| **Unfollowed you** | They dropped off your followers list. |
| **Gone from your following** | They dropped off *your* following list. Either you unfollowed them or, if you didn't, they removed you as a follower. Your own lists can't tell these apart. |
| **Gone from both lists** | They blocked you, or deactivated or deleted their account. |

Each entry shows when it happened (between which two snapshots), how long the follow had lasted, and where things stand now: *you still follow them*, *followed you again*, *still follows you*. Renamed accounts are matched by Instagram's account id (in fetched data), so a rename shows as "renamed from @old" rather than a fake unfollow.

## Setup

Requires Docker Desktop.

```powershell
copy .env.example .env        # then edit: POSTGRES_PASSWORD, TZ, EXPORTS_DIR
docker compose up -d --build
start http://localhost:47321/settings
```

On the Settings page, paste your Instagram `sessionid` cookie; the page explains where to find it. The app checks the cookie with one request before saving it. From then on it fetches once a day.

Both containers use `restart: unless-stopped`, so they come back whenever Docker Desktop starts. Turn on *Start Docker Desktop when you sign in* to make the daily run automatic. If the PC was off or asleep at the scheduled time, the run happens about a minute after Docker starts.

## How data gets in

1. **Automatic daily fetch.** At `DAILY_AT` (in the `TZ` time zone, plus up to 30 minutes of random delay), the app reads your lists through the endpoints instagram.com itself uses: one page of 50 at a time, with 2.5–6 s pauses. It stops at the first sign of trouble (expired session, checkpoint, rate limit). **Fetch now** on the Settings page runs one immediately.
   > ⚠️ This is unofficial and against Instagram's Terms of Use. Running it from your own PC keeps the requests on your home IP, the same as your browser. At under 1,000 accounts it's about 20 requests a day, which is low risk but not zero: Instagram can log the session out or ask you to confirm it's you. The report then shows a "Stop press" banner, and you paste a fresh cookie on the Settings page.
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
- The session cookie is effectively your Instagram login. It's stored in Postgres, encrypted with ASP.NET Core Data Protection. The keys live in a separate `keys` volume, so a database dump alone doesn't reveal the cookie. If that volume is lost, paste the cookie again.
- Postgres isn't published to the host; only the app container can reach it.

## Data

Two named volumes: `pgdata` (the database) and `keys` (the encryption keys).

- Back up the database: `docker compose exec db pg_dump -U following following > backup.sql`
- Wipe everything: `docker compose down -v`

The schema is in `src/Following.Web/Data/Migrations` and is applied automatically on startup.

## Limitations

- Renames are only recognized between two fetched snapshots. Exports don't include account ids, so a rename across an export shows as an unfollow plus a new follow.
- An account that's deactivated only temporarily shows as "gone from both lists", then as a new follow when it comes back.
- Exports and fetches can differ slightly (deactivated accounts, for example). Mixing them can produce a few spurious events around an import.
- Full names come only from fetches; exports don't include them.

## Development

```powershell
dotnet test
dotnet tool restore; dotnet ef migrations add <Name> -p src/Following.Web -o Data/Migrations
```

The projects are:
- `src/Following.Core`: the Instagram client, export parser, diff logic, schedule math and report template (`Report/report.html`)
- `src/Following.Web`: the ASP.NET Core app (Postgres via EF Core, daily scheduler, Settings page)
- `tests/Following.Tests`
