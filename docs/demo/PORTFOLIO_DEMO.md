# Portfolio Demo

## Run

Docker Desktop must be running. The script starts only the PostgreSQL service, builds Release binaries, restarts only ASTRA processes from this workspace, and executes the two portfolio scenarios.

```powershell
pwsh -File scripts/demo/Run-PortfolioDemo.ps1
```

For a quick rerun with an existing Release build:

```powershell
pwsh -File scripts/demo/Run-PortfolioDemo.ps1 -SkipBuild
```

`-SkipTcpVerification` omits only the existing HTTP/TCP cross-transport E2E. Gacha and incident-compensation verification still runs.

## Evidence

Each run overwrites the same bounded files:

- `output/demo/portfolio-demo-evidence.json`
- `output/demo/portfolio-demo-summary.md`
- `output/demo/portfolio-demo-tcp-e2e.log`

The evidence contains no access tokens or local signing keys. It records the active content checksum, exact idempotent replay checks, final wallet equation, incident target match, audit coverage, Outbox delivery delta, and TCP verification result.

Visual evidence and its QA notes are indexed in `output/playwright/README.md`.

## Stop

Stop only the five ASTRA application processes:

```powershell
pwsh -File scripts/demo/Stop-PortfolioDemo.ps1
```

Also stop PostgreSQL without deleting its volume:

```powershell
pwsh -File scripts/demo/Stop-PortfolioDemo.ps1 -StopPostgres
```

The scripts do not run Docker prune, remove volumes, start Redis, or start the observability profile. Existing observability containers are left unchanged.
