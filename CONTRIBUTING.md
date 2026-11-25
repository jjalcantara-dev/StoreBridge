# Contributing to StoreBridge

Notes for working on the library itself. Usage documentation lives in [README.md](README.md); maintainer and
architecture notes for AI coding agents live in [CLAUDE.md](CLAUDE.md).

## Building from source

```bash
git clone https://github.com/jjalcantara-dev/StoreBridge.git
cd StoreBridge

# Restore + build (net10.0)
dotnet restore
dotnet build --configuration Release

# Run the full test suite
dotnet test --configuration Release

# Just one project
dotnet test tests/StoreBridge.Apple.Tests   --configuration Release
dotnet test tests/StoreBridge.Android.Tests --configuration Release

# Produce NuGet packages locally
dotnet pack --configuration Release /p:Version=1.0.0-local --output ./nupkgs
```

Repo layout:

```
src/
  StoreBridge/             Core abstractions, models, enums
  StoreBridge.Apple/       App Store Server API v1 + Server Notifications v2
  StoreBridge.Android/     Android Publisher v3 + Pub/Sub
tests/
  StoreBridge.Apple.Tests/    xUnit v3 + FakeHttpMessageHandler
  StoreBridge.Android.Tests/  xUnit v3 + NSubstitute
samples/
  StoreBridge.Sample.Web/  Minimal ASP.NET Core harness
```

All tests are unit tests — there are no integration tests against live Apple/Google APIs. They do include a notification genuinely signed by Apple (verified against the real Apple Root CA - G3 chain) and seeded fuzz tests of every webhook entry point. Use the sample web app for end-to-end validation with sandbox credentials.

---

## Releasing new versions

Add an entry to [CHANGELOG.md](CHANGELOG.md) before tagging.

Releases are fully automated via GitHub Actions ([`.github/workflows/publish.yml`](.github/workflows/publish.yml)). Push a semver git tag and the workflow restores, builds, tests, packs all three NuGet packages, and pushes them to NuGet.org using [**Trusted Publishing**](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job trades its GitHub OIDC token for a short-lived NuGet API key, so no long-lived key is stored in the repository.

One-time setup (already done for this repository — needed again only for a fork or a new nuget.org owner):

1. On nuget.org → your account → **Trusted Publishing**, add a policy for this repository: owner `jjalcantara-dev`, repository `StoreBridge`, workflow file `publish.yml`.
2. In GitHub → repository **Settings → Secrets and variables → Actions**, add `NUGET_USER` with your nuget.org **profile name** (not your email).
3. After the first successful release, delete the old `NUGET_API_KEY` secret and revoke that key on nuget.org.

Then, **from an up-to-date `main`** (the tag decides which commit is published):

```bash
git checkout main && git pull
git tag v1.2.0
git push origin v1.2.0
```

The workflow extracts the version from the tag (`v1.2.0` → `1.2.0`) and passes it to both the build (assembly versions) and the pack (package version), so you don't edit `Version` in source.

## Conventions

- Keep the public API files (`src/*/PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`) in sync — the analyzer fails the build otherwise.
- New members of public enums are appended **after** `Other`, so existing numeric values never change.
- Test fixtures must mirror the real store payloads (e.g. Apple `lastTransactions` items have no `productId`; Google sends `eventTimeMillis` as a string).
- Never log raw purchase tokens or transaction IDs — use `LogRedaction.Token(...)`.
