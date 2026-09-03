# syntax=docker/dockerfile:1
#
# One Dockerfile for all four hosts (Api, Worker, Migrator, Provisioner) - they share the same
# dependency closure (Module -> Application, Infrastructure.Postgres -> Domain), so near-identical
# files would only be able to drift apart, not stay honestly in sync. Copied from ago-chat's own
# Dockerfile shape (20-20's brief: "follow ago-chat's own chiselled-base shape rather than
# inventing a second one") - same build-arg selection, same nuget-feed mount, same fixed-filename
# entrypoint trick, same final base image. Select the host with --build-arg PROJECT_NAME=Ago.Calendar.Api.
#
# `ago-root#363`: Ago.Calendar.Provisioner joins the other three here rather than getting its own
# file, for the identical reason the comment above already gives - it shares the same closure
# (Application, Infrastructure.Postgres), so a second Dockerfile would only be able to drift from
# this one. It never listens on a port and is never routed to by anything (see that project's own
# remarks); EXPOSE below is inert for it, exactly as it already is for Migrator.
#
# The local NuGet feed (ago-root/docs/runbooks/workspace.md) lives outside this repository, so it
# cannot be COPY'd from the normal build context - it is mounted in via Buildx's --build-context
# instead (`docker build --build-context nugetfeed=../.nuget-feed ...`).
#
# Deliberately does NOT carry ago-chat's Russian-Trusted-Root-CA step (its Dockerfile's `RUN apt-get
# install ca-certificates curl && curl ... gu-st.ru ...`): that cert exists solely for
# Ago.Chat.MaxApiClient's outbound calls to platform-api2.max.ru (14-02), and nothing in this
# repository makes an outbound call to any .ru-hosted service today - confirmed by grepping src/ for
# an HttpClient or a .ru host and finding neither. Copying that step here would be adding an
# unexplained external dependency this repository does not need; it belongs in this file the day a
# real caller does.

ARG PROJECT_NAME
# `15-06`-equivalent for this product: the commit this image is built from, baked into the compiled
# binary below via SourceRevisionId. Defaults to "unknown" rather than failing the build - a local
# `docker build` for a quick check is a legitimate thing to do, and it should say "unknown" out loud
# rather than lie or refuse.
ARG GIT_COMMIT=unknown

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_NAME
ARG GIT_COMMIT
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.docker.config ./
COPY src/Ago.Calendar.Api/Ago.Calendar.Api.csproj src/Ago.Calendar.Api/
COPY src/Ago.Calendar.Worker/Ago.Calendar.Worker.csproj src/Ago.Calendar.Worker/
COPY src/Ago.Calendar.Migrator/Ago.Calendar.Migrator.csproj src/Ago.Calendar.Migrator/
COPY src/Ago.Calendar.Provisioner/Ago.Calendar.Provisioner.csproj src/Ago.Calendar.Provisioner/
COPY src/Ago.Calendar.Module/Ago.Calendar.Module.csproj src/Ago.Calendar.Module/
COPY src/Ago.Calendar.Application/Ago.Calendar.Application.csproj src/Ago.Calendar.Application/
COPY src/Ago.Calendar.Contracts/Ago.Calendar.Contracts.csproj src/Ago.Calendar.Contracts/
COPY src/Ago.Calendar.Domain/Ago.Calendar.Domain.csproj src/Ago.Calendar.Domain/
COPY src/Ago.Calendar.Infrastructure.Postgres/Ago.Calendar.Infrastructure.Postgres.csproj src/Ago.Calendar.Infrastructure.Postgres/
COPY src/Ago.Calendar.Infrastructure.Redis/Ago.Calendar.Infrastructure.Redis.csproj src/Ago.Calendar.Infrastructure.Redis/
COPY src/Ago.Calendar.Infrastructure.Time/Ago.Calendar.Infrastructure.Time.csproj src/Ago.Calendar.Infrastructure.Time/

RUN --mount=type=bind,from=nugetfeed,target=/nuget-feed \
    dotnet restore "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" -r linux-x64 --configfile nuget.docker.config

COPY src/ src/
# -r linux-x64 --self-contained false: still framework-dependent (the base images below carry the
# runtime), but RID-restricted - the build stage is always this SDK image, always linux, so there is
# never a reason to publish for any other RID. Without this, a RID-agnostic publish ships every RID's
# native assets for every native-asset NuGet package in the dependency closure under /app/runtimes
# (see ago-chat's own Dockerfile and docs/backlog/8-04-container-publish-rid-trim.md for the
# ~440MB SkiaSharp instance of this that motivated the flag there).
# -p:SourceRevisionId=<sha>: the SDK appends "+<sha>" to AssemblyInformationalVersion, so the commit
# travels inside the assembly rather than beside it (adr/0047's mechanism, ported to this repository).
RUN --mount=type=bind,from=nugetfeed,target=/nuget-feed \
    dotnet publish "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" -c Release -o /app \
      -r linux-x64 --self-contained false --configfile nuget.docker.config \
      -p:SourceRevisionId="${GIT_COMMIT}"

# Bake the concrete DLL name into a fixed filename here, while the build stage still has a shell
# (the SDK image does) - the final stage below is Chiseled, which ships with no shell at all, so
# its ENTRYPOINT must be a literal exec-form array with no runtime `$VAR` expansion. `dotnet <dll>`
# resolves its host config from same-named companions next to the dll (.deps.json/.runtimeconfig.json),
# not just the dll itself - renaming only the dll leaves `dotnet` unable to find `app.deps.json`/
# `app.runtimeconfig.json` and it falls back to (and fails) the self-contained-app code path.
RUN cp "/app/${PROJECT_NAME}.dll" /app/app.dll \
 && cp "/app/${PROJECT_NAME}.deps.json" /app/app.deps.json \
 && cp "/app/${PROJECT_NAME}.runtimeconfig.json" /app/app.runtimeconfig.json

# Ubuntu Chiseled: same base ago-chat uses, and for the same reason (docs/backlog/8-00 there) -
# current .NET guidance's default recommendation for production with no special requirements,
# smaller than Alpine in practice, no shell/package manager (smallest attack surface), glibc-based
# so it sidesteps Alpine's musl-compatibility risk for native dependencies (Npgsql,
# StackExchange.Redis). adr/0054 assumes this non-root-by-inheritance property for the three
# Ago.Chat.* hosts; the same base gives Ago.Calendar.Api/-Worker/-Migrator the same property here.
# Ago.Calendar.Migrator is a console app and would run on the smaller `runtime` base, saving a few
# MB - it uses this one anyway, for the same reason ago-chat's migrator does: a second base behind a
# build-arg would fork the one property this file exists for, that three images sharing a dependency
# closure are built by one command that cannot drift. EXPOSE below is inert for it, and harmless.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG PROJECT_NAME
ARG GIT_COMMIT
# The OCI annotations a registry and `docker inspect`/`crane config` read (adr/0047). `.source` is
# not only documentation - GHCR uses it to link the published package back to this repository, which
# is what makes the package inherit the repository's own visibility instead of arriving orphaned.
LABEL org.opencontainers.image.source="https://github.com/golyakoff/ago-calendar" \
      org.opencontainers.image.description="AGO Calendar host: ${PROJECT_NAME}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "app.dll"]
