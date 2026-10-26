<img src="assets/icon.svg" width="64" height="64" alt="">

# Social Connection Tracker

Keeps a daily record of your connections on Instagram, Facebook and Steam, and tells you who dropped off. It runs on your PC in Docker (the app plus Postgres), and the report is served at <http://localhost:47321>.

| Platform | What's tracked | Where the data comes from |
|---|---|---|
| **Instagram** | Followers and following: who unfollowed you, who doesn't follow you back, who you don't follow back, every change | Fetched daily with your browser session, or official exports |
| **Facebook** | Friends: who is no longer your friend | Official exports only (Facebook has no API for friends lists) |
| **Steam** | Friends: who is no longer your friend | Fetched daily through Steam's official Web API |

## The report

- **Overview** (`/`): a card per platform with its counts and what changed since you last looked, then every platform's changes in one list. **Lost connections** lists just the people who unfollowed or unfriended you; **History** lists every change, newest first.
- **Platform pages** (`/instagram`, `/facebook`, `/steam`): one platform in detail. Instagram has tabs for *Unfollowed you*, *Not following you back*, *You don't follow back*, *Mutual* and *History*. Facebook and Steam have *No longer friends*, *Friends* and *History*.

Each run compares the new snapshot with the previous one, so something that happened yesterday is in today's report. Each entry shows when it happened (between which two snapshots), how long the follow or friendship had lasted, and where things stand now: *follows you again*, *friends again*.

What a lost connection means depends on what the platform's data can show:

| You see | It means |
|---|---|
| **Unfollowed you** (Instagram) | They dropped off your followers list. |
| **Blocked you, or deactivated their account** (Instagram) | They dropped off both your lists at once: they blocked you, or deactivated or deleted their account. |
| **No longer friends** (Facebook) | They're gone from your friends list: they unfriended you, or deactivated or deleted their account. Friends you removed yourself are listed in History as "you removed them" instead, since the export says so. |
| **No longer friends** (Steam) | They're gone from your friends list. Steam doesn't say who removed whom, so this includes people you removed. |

On Instagram, someone who drops off only *your* following list appears in History as "gone from your following", not as a lost connection. Usually you unfollowed them; if you didn't, they removed you as a follower. Your own lists can't tell these apart.

The summaries count the changes since you last pressed **Clear** (or, before that, since the previous snapshot), so a change stays visible until you've seen it, however many runs happen in between. Clearing on the overview clears every platform; on a platform page, just that one. It's remembered per browser.

Renamed Instagram accounts are matched by Instagram's account id (in fetched data), so a rename shows as "renamed from @old" rather than a fake unfollow. Steam friends are matched by their Steam id, so changing their profile name doesn't matter. Facebook exports list names only, so Facebook friends are matched by name.

## Setup

Requires Docker Desktop.

```powershell
copy .env.example .env        # then edit: POSTGRES_PASSWORD, TZ, EXPORTS_DIR
docker compose up -d --build
start http://localhost:47321/settings
```

Then connect the platforms you use on the Settings page, as described below. From then on the app runs once a day.

Both containers use `restart: unless-stopped`, so they come back whenever Docker Desktop starts. Turn on *Start Docker Desktop when you sign in* to make the daily run automatic. If the PC was off or asleep at the scheduled time, the run happens about a minute after Docker starts.

### Instagram: browser extension (Brave, Chrome, Edge)

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
- **No extension?** The Settings page still has a "paste the cookie manually" option. You can also skip fetching and only import exports (below).

### Steam: Web API key

1. Get a key at <https://steamcommunity.com/dev/apikey>. Any domain name will do, e.g. `localhost`.
2. Make your friends list public: in Steam, open *Edit Profile → Privacy Settings* and set *Friends List* to *Public*. The API answers like a stranger looking at your profile, so it can't read a private list.
3. On the Settings page, enter your profile (SteamID64, profile link, or custom URL name) and the key, and press **Connect**. The app checks both and takes the first snapshot right away.

### Facebook: exports

Facebook has no API for friends lists, so its data comes from the official export. Each export is one snapshot; comparing two shows who's no longer your friend, so import one now and another whenever you want to check.

On Facebook, open *Settings & privacy → Settings → Accounts Center → Your information and permissions → Download your information*. Pick your Facebook profile, then *Some of your information → Friends*, *Download to device*, date range *All time*, format **JSON**. Upload the zip on the Settings page, or let it land in `EXPORTS_DIR`.

## How data gets in

1. **Automatic daily fetch** (Instagram and Steam). At `DAILY_AT` (in the `TZ` time zone, plus up to 30 minutes of random delay), the app fetches every connected platform. **Refresh** on the report runs a fetch immediately (the overview fetches every connected platform; a platform page, just that one), as does **Fetch now** on the Settings page. The report reloads when it finishes.
   - **Instagram:** the app reads your lists through the endpoints instagram.com itself uses: one page of 50 at a time, with 2.5–6 s pauses. It stops at the first sign of trouble (expired session, checkpoint, rate limit).
     > ⚠️ This is unofficial and against Instagram's Terms of Use. Running it from your own PC keeps the requests on your home IP, the same as your browser. At under 1,000 accounts it's about 20 requests a day, which is low risk but not zero: Instagram can log the session out or ask you to confirm it's you. Logging in again on instagram.com in your browser fixes it; the extension passes the new session on.
   - **Steam:** two requests to the official Web API: the friends list, then everyone's profile names.
2. **Official exports** (Instagram and Facebook). Upload the zip on the Settings page, or let it land in `EXPORTS_DIR` (mounted read-only): every `instagram-*.zip` and `facebook-*.zip` there is imported on the next daily run. The app tells Instagram and Facebook exports apart by what's inside. For Instagram, request *Some of your information → Followers and following* in JSON format from the Instagram app's *Accounts Center → Your information and permissions → Download your information*. Exports include the exact date each follow or friendship started.

A fetch is refused, and not saved, if a list comes back much shorter than the count on your Instagram profile, or empty when the previous one wasn't. A truncated list would otherwise show up as a wave of fake "unfollowed you" or "no longer friends" events.

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
- The Instagram session cookie is effectively your Instagram login, and the Steam Web API key acts on your Steam account's behalf. Both are stored in Postgres, encrypted with ASP.NET Core Data Protection. The keys live in a separate `keys` volume, so a database dump alone doesn't reveal them. If that volume is lost, the extension resends the cookie within the hour; enter the Steam key again.
- Postgres isn't published to the host; only the app container can reach it.

## Data

Two named volumes: `pgdata` (the database) and `keys` (the encryption keys).

- Back up the database: `docker compose exec db pg_dump -U following following > backup.sql`
- Wipe everything: `docker compose down -v`

The schema is in `src/ConnectionTracker.Web/Data/Migrations` and is applied automatically on startup. Data from before Facebook and Steam support is kept and labelled as Instagram's. The database and its user are still called `following`, after the app's old name, so existing installs keep working.

## Limitations

- Instagram renames are only recognized between two fetched snapshots. Exports don't include account ids, so a rename across an export shows as an unfollow plus a new follow.
- Facebook friends are matched by name. Someone who changes their name shows up as one friend lost and one new friend. Two friends with the same name are told apart only by their order in the export.
- An account that's deactivated only temporarily shows as lost, then as new when it comes back.
- Instagram exports and fetches can differ slightly (deactivated accounts, for example). Mixing them can produce a few spurious events around an import.
- Instagram full names come only from fetches; Instagram exports don't include them.

## Development

```powershell
dotnet test
dotnet tool restore; dotnet ef migrations add <Name> -p src/ConnectionTracker.Web -o Data/Migrations
```

The projects are:
- `src/ConnectionTracker.Core`: the platform clients (`Instagram/`, `Steam/`), export parsers (`Instagram/`, `Facebook/`), diff logic, schedule math and report template (`Report/report.html`)
- `src/ConnectionTracker.Web`: the ASP.NET Core app (Postgres via EF Core, daily scheduler, Settings page)
- `tests/ConnectionTracker.Tests`

## License

[MIT](LICENSE)
