<img src="assets/icon.svg" width="64" height="64" alt="">

# following

A daily report of your Instagram followers and following. It shows who doesn't follow you back, who you don't follow back, and every follow and unfollow between days. Everything stays on your machine. The report is one self-contained `report.html` you open in a browser.

## Who unfollowed you

Each run compares the new snapshot with the previous one, so something that happened yesterday is in today's report. The **Unfollowed you** tab opens by default whenever there's fresh news. It labels each case with what the data can actually show:

| You see | It means |
|---|---|
| **Unfollowed you** | They dropped off your followers list. |
| **Gone from your following** | They dropped off *your* following list. Either you unfollowed them or, if you didn't, they removed you as a follower. Your own lists can't tell these apart. |
| **Gone from both lists** | They blocked you, or deactivated or deleted their account. |

Each entry shows when it happened (between which two snapshots), how long the follow had lasted, and where things stand now: *you still follow them*, *followed you again*, *still follows you*. Renamed accounts are matched by Instagram's account id (in fetched data), so a rename shows as "renamed from @old" rather than a fake unfollow.

## How data gets in

There are two sources, and you can use either or both:

1. **Automatic daily fetch.** `following login` stores your browser's `sessionid` cookie, encrypted with Windows DPAPI. A scheduled task then reads your lists once a day through the endpoints instagram.com itself uses: one page of 50 at a time, with 2.5–6 s pauses. It stops at the first sign of trouble (expired session, checkpoint, rate limit) and never retries aggressively.
   > ⚠️ This is unofficial and against Instagram's Terms of Use. At under 1,000 accounts it's about 20 requests a day, which is low risk but not zero: Instagram can log the session out or ask you to confirm it's you. When that happens the report shows a "Stop press" banner. Run `following login` again to continue.
2. **Official export.** In the Instagram app go to *Accounts Center → Your information and permissions → Download your information*. Choose *Some of your information → Followers and following*, format **JSON**. Any `instagram-*.zip` that lands in your Downloads folder is imported on the next daily run. You can also run `following import <zip>`. Exports include the exact date each follow happened.

A fetch is refused, and not saved, if the list comes back much shorter than the count on your profile. A truncated list would otherwise show up as a wave of fake "unfollowed you" events.

## Setup

Requires the .NET 10 SDK.

```powershell
./publish.ps1                                   # builds publish\following.exe + following-background.exe
./publish/following.exe login                   # paste your sessionid cookie (input is hidden)
./publish/following.exe fetch                   # first snapshot
./publish/following.exe schedule install --at 09:00
./publish/following.exe open                    # view the report
```

The scheduled task is per-user and needs no admin rights. It runs `following-background.exe`, which has no console window, so nothing pops up. If the PC was off at the scheduled time, the task runs once you're back. It adds up to 30 minutes of random delay so the request time varies from day to day.

## Commands

| Command | |
|---|---|
| `login` / `logout` | Store or forget the session cookie (logout = exports-only mode) |
| `fetch` | Fetch now and rebuild the report |
| `import <zip\|folder> [--force]` | Import an official export |
| `daily` | What the scheduled task runs: import new exports, fetch (skipped if one succeeded in the last 12 h), rebuild the report |
| `report [--open]` / `open` | Rebuild or open `report.html` |
| `status` | Data folder, session, snapshots, last runs |
| `schedule install [--at HH:mm]` / `remove` / `status` | Manage the Windows scheduled task |

Every command accepts `--data <folder>`.

## Data

Everything lives in `%LOCALAPPDATA%\following` (override with `--data` or `FOLLOWING_DATA`):

- `snapshots/*.json`: one file per fetch or import with the full lists. Delete a file to drop that snapshot.
- `report.html`: the report
- `config.json`: username, encrypted cookie, watched export folders (`exportDirs`)
- `state.json`: imported zip hashes and the run history
- `following.log`: log of every run

## Limitations

- Renames are only recognized between two fetched snapshots. Exports don't include account ids, so a rename across an export shows as an unfollow plus a new follow.
- An account that's deactivated only temporarily shows as "gone from both lists", then as a new follow when it comes back.
- Exports and fetches can differ slightly (deactivated accounts, for example). Mixing them can produce a few spurious events around an import.
- Full names come only from fetches; exports don't include them.

## Development

```powershell
dotnet test
```

The projects are `src/Following.Core` (all logic and the report template in `Report/report.html`), `src/Following` (the CLI), `src/Following.Background` (the windowless scheduled runner) and `tests/Following.Tests`.
