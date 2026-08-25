# AGO Calendar

[![CI](https://github.com/golyakoff/ago-calendar/actions/workflows/ci.yml/badge.svg)](https://github.com/golyakoff/ago-calendar/actions/workflows/ci.yml)

The second product on AGO Platform: booking and scheduling. A shop publishes its services and
working hours, customers book a slot, operators confirm or reject from a queue.

This repository holds the product's Domain, Application, Contracts, Infrastructure and Module, plus
the two deployables built from them: `Ago.Calendar.Api` (commands, queries) and
`Ago.Calendar.Worker` (consumers, outbox dispatch, scheduled jobs). There is deliberately no
`Webhooks` host - nothing here needs an outbound-delivery bulkhead yet the way AGO Chat's CRM
integrations do (`../ago-root/docs/adr/0013-*`).

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

`nuget.config` restores `Ago.Platform.*` from the local file feed
(`../ago-root/docs/runbooks/workspace.md`); pack `ago-platform` into it first if it is empty. CI
uses `nuget.ci.config` and the real GitHub Packages feed instead (`../ago-root/docs/adr/0018-*`).

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
