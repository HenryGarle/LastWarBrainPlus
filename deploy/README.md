# Hosting LastWar on one Hetzner server

One server runs everything with Docker Compose: Postgres 18, the app (LastWar.Server with the web
UI inside), Caddy for HTTPS, and a nightly database dump. New versions go live with one command
from the PC: `powershell -File deploy\deploy.ps1`.

| file | what |
|---|---|
| `deploy.ps1` | on the PC: pack, upload, build on the server, swap (also `-Rollback`) |
| `pack.ps1` | on the PC: packs the code, the data files and (with `-Dump`) the database |
| `docker-compose.yml` | the four services |
| `Caddyfile` | HTTPS for your domain, forwards to the app |
| `.env.example` | the settings; the real `.env` lives only on the server |
| `../Dockerfile` | builds the image (web UI + server) |

The hosted copy uses **guests only**: nothing packed includes `data\runners` or `data\sessions` (the
real accounts' sessions), `.env` or the old SQLite file.

**On the server**

```
/opt/lastwar/.env          settings - yours, never overwritten
/opt/lastwar/data/         the app's files          /opt/lastwar/backups/      nightly dumps
/opt/lastwar/LastWar/      the live code            /opt/lastwar/LastWar.prev/ the one before
```

The database is in a Docker volume (`lastwar_pg`); deploys never touch it.

## 0. SSH key (once, on the PC)

The scripts call `ssh`/`scp` several times per deploy; with only a root password you would type it
each time. A key signs in by itself:

```powershell
ssh-keygen -t ed25519                         # Enter for the defaults; a passphrase is optional
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh root@<ip> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"
ssh root@<ip> echo ok                         # must print ok WITHOUT asking for the password
```

Then switch password logins off (only after the line above works):

```sh
echo "PasswordAuthentication no" > /etc/ssh/sshd_config.d/10-no-passwords.conf && systemctl reload ssh
```

## 1. Hetzner (once)

1. Cloud Console → new project → **Security → SSH keys**: add your public key
   (on the PC: `ssh-keygen -t ed25519`, then paste `%USERPROFILE%\.ssh\id_ed25519.pub`).
2. **Create server**: Nuremberg / Falkenstein / Helsinki, **Ubuntu 24.04**, **CX33** (4 vCPU, 8 GB, 80 GB),
   your SSH key, **Backups** on.
3. **Firewall** on the server: inbound TCP 22 from your home IP, TCP 80 + 443 and UDP 443 from anywhere.
   Nothing else - Postgres is never exposed.
4. **DNS**: an A record for your domain → the server's IPv4. No domain yet? `<ip-with-dashes>.sslip.io`
   (e.g. `203-0-113-7.sslip.io`) works to start with.
5. On the server (`ssh root@<ip>`): `curl -fsSL https://get.docker.com | sh`
6. On the PC: put `root@<ip>` in `C:\Temp\LWC\LastWar\deploy\server.txt`.

## 2. First deploy

From `C:\Temp\LWC\LastWar` on the PC:

```powershell
powershell -File deploy\deploy.ps1 -WithData     # uploads code + data files; stops: "FIRST RUN: fill in .env"
ssh root@<ip> nano /opt/lastwar/.env             # DOMAIN, POSTGRES_PASSWORD (openssl rand -base64 30), SCHEDULER=off
powershell -File deploy\deploy.ps1               # builds (a few minutes) and starts everything, empty database
```

Then bring all the data across (once):

```powershell
powershell -File deploy\deploy.ps1 -MigrateDatabase
```

It pauses the local hourly pulse (and waits if one is running), dumps the local database, uploads it,
replaces the server's database with it, restarts the app, and prints the row count and the
**FIRST ADMIN PASSWORD** for "Owner". No password shown? On the server:
`cd /opt/lastwar/LastWar/deploy && docker compose run --rm app --new-admin Henry` (also the fix if you
are ever locked out).

Open `https://<your domain>`, sign in with it, and in **Access** make your own admin password and
viewer passwords for others. Delete "Owner" once yours works.

## 3. Check a guest works from the server, then turn the schedule on

The game now sees a data-centre address, not your home connection. Before anything runs by itself:

1. **Scheduled** → activity pulse → **Run now** (runs by hand with the scheduler off). It should finish
   with answered counts like at home.
2. Good? In `/opt/lastwar/.env` set `SCHEDULER=on`, then `powershell -File deploy\deploy.ps1` (or on the
   server `cd /opt/lastwar/LastWar/deploy && docker compose up -d`).
3. Keep the local copy paused (or stop the local server): one collector at a time.

## Updating - one command

```powershell
powershell -File deploy\deploy.ps1             # pack, upload, build on the server, swap
powershell -File deploy\deploy.ps1 -Logs       # ...then watch the log
powershell -File deploy\deploy.ps1 -WithData   # ...also re-send assets / command list / migration CSVs
powershell -File deploy\deploy.ps1 -Rollback   # back to the version before the last deploy
```

The new image is built while the old version keeps serving; the swap is the few seconds the app
takes to restart. A failed build leaves the running version alone. New tables or columns are added
by the app itself when it starts.

## Backups and restore

- **Nightly dump**: `/opt/lastwar/backups/lastwar_<date>.dump`, 14 kept (`BACKUP_DAYS`). Hetzner's
  server backups include this folder.
- **Off the server**: copy one home now and then
  (`scp root@<ip>:/opt/lastwar/backups/<newest>.dump C:\Temp\LWC\backups\`). A Hetzner Storage Box
  with a nightly `rsync` is the automatic version - not set up yet.
- **Restore** (replaces the database):
  ```sh
  cd /opt/lastwar/LastWar/deploy && docker compose stop app
  docker compose exec -T db pg_restore -U lastwar -d lastwar --clean --if-exists --no-owner < /opt/lastwar/backups/<file>.dump
  docker compose start app
  ```

## Reading the database from the PC

Postgres only listens on the server itself (`127.0.0.1:5432`), and the firewall has no database port.
Reach it through SSH instead - encrypted, nothing extra exposed:

```powershell
ssh -N -L 5433:127.0.0.1:5432 root@<ip>      # leave this window open while you use it
```

Then connect any tool (psql, DBeaver, pgAdmin, Excel, the local code) to `localhost:5433`, database
`lastwar`, schema `lastwar`. Use a read-only login rather than the app's own - make one once:

```sh
cd /opt/lastwar/LastWar/deploy
docker compose exec db psql -U lastwar -d lastwar -c "
  CREATE ROLE reader LOGIN PASSWORD '<a long password>';
  GRANT CONNECT ON DATABASE lastwar TO reader;
  GRANT USAGE ON SCHEMA lastwar TO reader;
  GRANT SELECT ON ALL TABLES IN SCHEMA lastwar TO reader;
  ALTER DEFAULT PRIVILEGES FOR ROLE lastwar IN SCHEMA lastwar GRANT SELECT ON TABLES TO reader;
  ALTER ROLE reader SET search_path = lastwar;
  ALTER ROLE reader SET default_transaction_read_only = on;"
```

Connection string for code: `Host=localhost;Port=5433;Username=reader;Password=...;Database=lastwar`.

## Handy

```sh
cd /opt/lastwar/LastWar/deploy
docker compose ps                   # what is running
docker compose logs -f app          # the app's log (warnings and errors only)
cat ../VERSION                      # which deploy is live
df -h /                             # disk (the database grows by roughly 15k rows an hour)
```

## Copy the server's data down to this machine

    powershell -File deploy\deploy.ps1 -PullDatabase

Replaces the local database with the server's (about 220 MB, a few minutes). Before it does:
the local database is saved to C:\Temp\LWC\backups\local_before_pull_<time>.dump, and any table with
more rows locally than on the server (local chat, favourites) has its local-only rows put back
afterwards - rows that clash with a server row on a key are listed, not guessed. It stops and
restarts the local server and turns every local scheduled job off, so only the server collects.
The server's dump is kept too (server_<time>.dump); delete old ones from backups\ when you like.
