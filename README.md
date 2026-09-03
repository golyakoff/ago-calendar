# AGO Calendar

[![CI](https://github.com/golyakoff/ago-calendar/actions/workflows/ci.yml/badge.svg)](https://github.com/golyakoff/ago-calendar/actions/workflows/ci.yml)

The second product on AGO Platform: booking and scheduling. A shop publishes its services and
working hours, customers book a slot, operators confirm or reject from a queue.

This repository holds the product's Domain, Application, Contracts, Infrastructure and Module, plus
the two deployables built from them: `Ago.Calendar.Api` (commands, queries) and
`Ago.Calendar.Worker` (consumers, outbox dispatch, scheduled jobs - `AvailabilityMaterializationJob`
is the first). There is deliberately no `Webhooks` host - nothing here needs an outbound-delivery
bulkhead yet the way AGO Chat's CRM integrations do (`../ago-root/docs/adr/0013-*`).

It consumes `Ago.Platform.*` as NuGet packages and **never** reaches into the platform's source -
except through the dev override below, which must never survive to a merged branch.

## It is not AGO Chat, and cannot become it

`../ago-root/docs/adr/0027-*` decides that AGO Calendar defines its own `Operator`, its own
permission vocabulary and its own tables; the two products are unified only at the identity layer,
through Keycloak. `Ago.Calendar.Architecture.Tests` holds that as a rule rather than as prose: **no
`Ago.Calendar.*` assembly may reference any `Ago.Chat.*` assembly.** Unlike the platform boundary,
nothing makes that true by construction - `ago-chat` is a sibling folder on disk, one
`ProjectReference` away - so the test ships with a deliberately violating fixture next to a
compliant twin, permanently in the build, proving the rule can go red.

## Rules

- Layering and what goes where: `../ago-root/docs/architecture/clean-architecture.md`
- Why Calendar is a second product, not a feature of AGO Chat: `../ago-root/docs/adr/0027-*`
- Why the platform is a package: `../ago-root/docs/adr/0012-*`
- Decisions: `../ago-root/docs/adr/`
- Working agreements: `../ago-root/CLAUDE.md`
- Full project layout: `../ago-root/docs/conventions/naming-and-structure.md`

## Build

```bash
cd C:/git/ago/ago-calendar
dotnet restore Ago.Calendar.slnx
dotnet format Ago.Calendar.slnx --verify-no-changes
dotnet build Ago.Calendar.slnx --no-restore -c Release
dotnet test Ago.Calendar.slnx --no-build -c Release
```

`nuget.config` declares `nuget.org` only; `Ago.Platform.*` resolves through a workspace-level
`NuGet.Config` one directory above every sibling checkout (`C:\git\ago\NuGet.Config`,
`../ago-root/docs/runbooks/workspace.md`) that names the local file feed - pack `ago-platform` into
it first if it is empty. CI uses `nuget.ci.config` and the real GitHub Packages feed instead
(`../ago-root/docs/adr/0018-*`); so does Dependabot, via `dependabot.yml`'s own `registries:` block
(`17-11`, `golyakoff/ago-root#396`).

## Database

Its **own** Postgres database, never a schema inside AGO Chat's (`../ago-root/docs/adr/0027-*`), and
no query in either product can reach the other's tables. Schema and reasoning:
`../ago-root/docs/architecture/data-model.md`, "AGO Calendar" section, and
`../ago-root/docs/adr/0049-*` for the two decisions worth arguing with - how time is stored, and why
"no double booking" is a database exclusion constraint rather than a check in the aggregate.

`dotnet ef` needs a connection string, and it comes from an environment variable so that no
credential shape is ever committed here:

```bash
cd C:/git/ago/ago-calendar
export AGO_CALENDAR_CONNECTION_STRING="Host=localhost;Port=5432;Database=ago_calendar;Username=...;Password=..."
dotnet ef migrations add <StageVerbSubject> \
  -p src/Ago.Calendar.Infrastructure.Postgres -s src/Ago.Calendar.Infrastructure.Postgres
```

`migrations add` never actually connects - schema generation is static from the model - so any
syntactically valid string satisfies it; `database update` needs a reachable one.

`Ago.Calendar.Integration.Tests` and `Ago.Calendar.Concurrency.Tests` each need a **running Docker
daemon**: they start a real Postgres through Testcontainers and apply the migrations from scratch.
That is not a preference - the overlap guarantee and the "two replicas cannot both materialise the
same day" guarantee are both storage-level constraints, and no in-memory provider has one to prove.

## The booking claim

`POST /api/v1/calendars/{calendarId}/events/{eventId}/book` is the product's only public write
surface, and it is **unauthenticated by design** - a customer books with a phone number and no
account. What stands in for authentication is the claim itself: a single
`UPDATE events SET status = 'PendingConfirmation' ... WHERE id = @id AND calendar_id = @calendarId
AND status = 'Available' AND starts_at > @now`, whose rows-affected count *is* the verdict, plus two
rate-limit buckets (per phone, per calendar) treated as correctness properties with tests rather than
as settings. A row count of zero is an ordinary outcome - somebody else got there first - and is
never logged at `Error`, never a 500 (`../ago-root/docs/adr/0059-*`).

The customer is told they are booked. The row says `PendingConfirmation` with a deadline, and an
operator may still veto it (`20-04`). That gap is the product spec's central design decision, and
`BookingConfirmedResponse` is shaped so no endpoint can leak it: no status field, no deadline field,
and a test that serialises a real response and fails if the JSON mentions either.

`Ago.Calendar.Infrastructure.Redis` exists for the rate limiter alone - Redis is never a source of
truth here, and the claim reads nothing from it.

## Where wall clock becomes an instant

Exactly one place: `Ago.Calendar.Infrastructure.Time`, whose only type is `SystemWallClockResolver`.
A `WorkingHoursRule` is a statement about a clock on a wall; an `Event` is an instant; the tz
database that bridges them is ambient machine state, so it sits behind a port like every other
external resource (`../ago-root/docs/adr/0049-*`, `../ago-root/docs/adr/0053-*`).

It is its own assembly so the rule is enforceable rather than merely written down:
`TimeZoneIsolationTests` asserts that **`System.TimeZoneInfo` is referenced by exactly one product
assembly**. A second conversion anywhere would compile, pass everything else, and be wrong twice a
year.

## Dev override

For a change that genuinely spans this repository and `ago-platform`, set `AgoCalendarDevOverride`
to build against a sibling `../ago-platform` checkout instead of the published package:

```bash
cd C:/git/ago/ago-calendar
AgoCalendarDevOverride=true dotnet build
```

It is named for the repository that honours it, not for what it points at: `ago-chat` has its own
`AgoPlatformDevOverride` doing the same job there. Two names rather than one shared name is
deliberate - a single variable would retarget *both* products in the same shell, so overriding here
would silently build AGO Chat against unpublished platform source too.

**A branch that gets merged must build against the published package.** CI never sets this
variable, so a branch left in override mode fails in CI even if it built locally - that failure is
the check catching exactly the API break the package boundary exists to catch.
