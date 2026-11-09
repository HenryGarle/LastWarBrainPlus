# Hosting

- Hetzner VPS `2.29.53.222` (root, key auth), Ubuntu, Docker + Compose. 4 vCPU, 7.6 GB RAM, 75 GB disk.
- LastWar: compose project `lastwar` in `/opt/lastwar`; containers lastwar-app-1 (.NET, port 8080 behind Caddy),
  lastwar-db-1 (postgres:18, 127.0.0.1:5432), lastwar-caddy-1 (80/443, lastwarbrain.dev), lastwar-backup-1 (nightly dump).
  Network lastwar_default. Data under /opt/lastwar/data. Deploy scripts: deploy/ (see deploy/README.md).
- Disk: 2026-10-08 the Docker build cache had grown to 30 GB and filled the disk; cleared, and deploy.ps1 now prunes
  build cache older than 72 h. DB went 17 GB -> 8.7 GB after the retention thinning (activity-and-retention.md).
  Watch `df -h /`; backups also grow.

## riftfall (Henry's second app, co-hosted 2026-10-08)

- Own folder `/opt/riftfall`, compose `name: riftfall`, own published port (e.g. 8081), joins `lastwar_default` to reach
  Postgres. Shares the Postgres server but has its own database and role `riftfall` (CONNECTION LIMIT 20, statement
  and idle-in-transaction timeouts 60 s); password in `/opt/riftfall/.env` (mode 600). `riftfall` cannot connect to
  the `lastwar` database.
- Its rules: never touch /opt/lastwar or lastwar-* containers/volumes; no global docker prunes; service names must not
  be db/app/caddy/backup (they would become DNS names on lastwar_default and Caddy would route to them); build images
  off the server; cap memory/CPU; it needs its own backup container.
- The full brief handed to that project's agent was "cohost-second-app-on-lastwar-vps.md" (given to Henry 2026-10-08).
