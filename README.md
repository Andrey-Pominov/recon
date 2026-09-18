# recon

Reconciliation of a ledger against a payment provider's reports. .NET 10, PostgreSQL. A consumer of [`ledger`](https://github.com/Andrey-Pominov/ledger)'s outbox.

Two systems book the same money — ours and theirs — and they disagree more often than anyone likes. This service follows the ledger's event feed for clearings, imports the provider's files, compares the two sides, and keeps every disagreement it ever found so that "what changed since yesterday" is a query rather than a diff of two spreadsheets.

## What it does

- **Follows the ledger.** `POST /ledger/sync` reads the ledger's `GET /events` from a stored cursor and keeps the clearings — `entry.posted` events that settle a hold. The cursor advances past everything else. Re-syncing is a no-op.
- **Imports provider reports.** A CSV per file; the same bytes imported twice are the same report. A malformed file is rejected with the line number and is not imported at all.
- **Compares and records.** A run compares everything known on both sides and writes every discrepancy, in one of six categories:

  | category | meaning |
  |---|---|
  | `amount_mismatch` | same reference, different amount |
  | `currency_mismatch` | same reference, different currency — reported instead of an amount mismatch, not as well |
  | `date_mismatch` | same reference and amount, booked further apart than the tolerance (default two days — clearings book late, that is normal) |
  | `missing_on_provider` | we cleared it; they have not reported it |
  | `missing_in_ledger` | they booked it; we have no such clearing |
  | `duplicate_on_provider` | they reported one reference more than once |

- **Runs are snapshots, and they are idempotent.** The inputs to a run — ledger position, set of reports, tolerance — are hashed. The same inputs return the run that already exists. A new report or a new clearing makes a new run with its own full set of findings. Nothing is updated in place.
- **Resolution is a difference between snapshots.** `GET /runs/transitions` says, for every run, how many discrepancies appeared and how many from the previous run went away. A clearing the provider reports a day late shows up as `missing_on_provider` in one run and as `resolved: 1` in the next.
- **On a schedule.** A worker syncs and runs every `Recon:IntervalMinutes`; a run over unchanged inputs costs nothing.

## Run it

`docker compose up` builds the ledger from its repository and starts both services with their databases:

```bash
docker compose up --build       # ledger on :8088, recon on :8090
```

Then, end to end — a few clearings in the ledger, a provider file that gets two of them wrong, and a second file the next day:

```bash
L=localhost:8088; R=localhost:8090; J='content-type: application/json'

# accounts and a top-up in the ledger, then three authorizations cleared as clr-1..3
FUND=$(curl -s -X POST $L/accounts -H "$J" -d '{"name":"funding","currency":"EUR","allowNegative":true}' | jq -r .id)
ALICE=$(curl -s -X POST $L/accounts -H "$J" -d '{"name":"alice","currency":"EUR"}' | jq -r .id)
SETTLE=$(curl -s -X POST $L/accounts -H "$J" -d '{"name":"settlement","currency":"EUR"}' | jq -r .id)
curl -s -X POST $L/entries -H "$J" -d "{\"idempotencyKey\":\"fund-1\",\"description\":\"top up\",\"postings\":[{\"accountId\":\"$FUND\",\"amount\":-10000},{\"accountId\":\"$ALICE\",\"amount\":10000}]}" > /dev/null
for n in 1 2 3; do
  H=$(curl -s -X POST $L/holds -H "$J" -d "{\"idempotencyKey\":\"auth-$n\",\"accountId\":\"$ALICE\",\"amount\":1000}" | jq -r .hold.id)
  curl -s -X POST $L/holds/$H/capture -H "$J" -d "{\"idempotencyKey\":\"clr-$n\",\"toAccountId\":\"$SETTLE\",\"amount\":700}" > /dev/null
done

curl -s -X POST $R/ledger/sync                       # {"added":3}

# day 1: the provider has clr-1 right, clr-2 for 7.30 instead of 7.00, no clr-3, and a clr-7 we never made
NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)
printf 'provider_ref,our_ref,amount,currency,booked_at\nPRV-1,clr-1,700,EUR,%s\nPRV-2,clr-2,730,EUR,%s\nPRV-7,clr-7,99,EUR,%s\n' $NOW $NOW $NOW \
  | curl -s -X POST "$R/reports?name=day-1.csv" --data-binary @-
RUN=$(curl -s -X POST $R/runs | jq -r .id)           # matched 1, discrepancies 3
curl -s $R/runs/$RUN/findings | jq -c '.[] | {category, ref, ledgerAmount, providerAmount}'

# day 2: clr-3 arrives
printf 'provider_ref,our_ref,amount,currency,booked_at\nPRV-3,clr-3,700,EUR,%s\n' $NOW \
  | curl -s -X POST "$R/reports?name=day-2.csv" --data-binary @-
curl -s -X POST $R/runs | jq '{matched, discrepancies}'     # matched 2, discrepancies 2
curl -s $R/runs/transitions | jq -c '.[] | {opened, resolved}'   # ..., {"opened":0,"resolved":1}
```

Tests start their own PostgreSQL through Testcontainers and fake the ledger in memory; Docker must be running.

```bash
dotnet test
```

## API

| Method | Path | |
|---|---|---|
| `POST` | `/ledger/sync` | pull new clearings from the ledger's outbox |
| `POST` | `/reports?name=` | import a provider CSV (request body) |
| `GET` | `/reports` | |
| `POST` | `/runs?dateToleranceHours=` | compare both sides now; returns the existing run if nothing changed |
| `GET` | `/runs` · `/runs/latest` · `/runs/{id}` | |
| `GET` | `/runs/{id}/findings?category=` | the run's discrepancies |
| `GET` | `/runs/transitions` | opened / resolved per run against the previous one |

Provider CSV: `provider_ref,our_ref,amount,currency,booked_at` — amount in minor units, `booked_at` ISO-8601. `our_ref` is the reference the provider echoes back; on our side it is the clearing's idempotency key.

## Design decisions

**The comparison is a pure function.** `Matcher.Compare(ours, theirs, tolerance)` takes two lists and returns findings. No database, no I/O. Every category is a three-line test, and the persistence layer only has to prove it stored what the matcher returned.

**A run is a snapshot, not a mutation.** The tempting model is a `findings` table with a `status` column that flips to `resolved`. Then "resolved" is a fact someone wrote, not a fact about the money. Here every run writes its complete set of discrepancies and nothing is ever updated; a discrepancy that was in run N and is not in run N+1 has resolved, and the `run_transitions` view says so. The history is the audit trail, and it cannot be edited into a better story.

**Runs are idempotent on their inputs.** The ledger position, the set of reports and the tolerance are hashed into `input_sha256`, unique on `runs`. A scheduler that fires every hour when nothing has changed writes nothing. A concurrent trigger — the worker and a human at the same moment — is serialized by an advisory lock and produces one row.

**The reference is the idempotency key.** The provider echoes back the reference we sent when we asked them to clear; that reference is what we used as the capture's idempotency key in the ledger. One string, two systems, no mapping table.

**Currency before amount.** A wrong currency almost always comes with a "wrong" amount. Reporting both would double-count one root cause, so a currency mismatch pre-empts the amount check.

**Tolerance is a parameter of the run, not a constant.** Card clearings book a day or two after authorization; comparing dates exactly would flag everything. The tolerance is part of the run's input hash, so tightening it is a new run with a new set of findings, not a rewrite of the old one.

**Everything imported is immutable.** Ledger records, provider records, reports, runs, findings — all refuse `UPDATE` and `DELETE` through triggers. The only row that moves is the ledger cursor, and it is a cursor.

## Not in scope, deliberately

- Windowing runs by period. Every run compares everything; at some volume runs should be bounded to a date range.
- Provider-specific file formats. The CSV is deliberately minimal; a real provider gets an adapter in front of the parser.
- Matching on anything but the reference — amount-and-date heuristics for references that got lost.
- Acting on findings: nothing here writes back to the ledger or opens a case. It reports.

## Layout

```
db/migrations/001_init.sql     both sides, runs, findings, the run_transitions view, immutability triggers
src/Recon.Core/                Matcher (pure), ProviderReportParser, LedgerHttpSource, ReconService, Migrator
src/Recon.Api/                 minimal API, ReconWorker on a timer
tests/Recon.Tests/             matcher and parser without a database; service and HTTP on PostgreSQL with a faked ledger
docker-compose.yml             ledger (built from its repository) + recon, each with its own PostgreSQL
```

## Working on it

`main` is what is released; `develop` is where work lands. Branch from `develop` as `feature/…`, `fix/…`, `chore/…`, `docs/…`, open a pull request back into `develop`. `main` only takes pull requests from `develop` — a required check enforces it. Commits follow Conventional Commits: `type(scope): description`.
