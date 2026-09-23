# Development Guide

This guide will help you set up a minimal local development environment for Jiten.

## Prerequisites

- **.NET 9.0+ SDK** & **ASP.NET Core Runtime 9.0**
- **Node.js** with **pnpm**
- **Docker & Docker Compose** (Recommended)
  - *Alternative:* Manual installations of Redis and PostgreSQL.


## 1. Acquire Files
Place these in a consistent location (e.g., a `data/` folder in the root, though the instructions below assume manual paths).
1. **JMdict DTD**: Save the contents of [jmdict_dtd.xml](https://www.edrdg.org/jmdict/jmdict_dtd_h.html) (from `<?xml...` to the end) as `jmdict_dtd.xml`.
2. **JMdict (Full)**: Download from [EDRDG](ftp://ftp.edrdg.org/pub/Nihongo/JMdict.gz) and extract the `.gz` file.
3. **JmdictFurigana**: Download `JmdictFurigana.json` from the [latest GitHub release](https://github.com/Doublevil/JmdictFurigana/releases/latest).
4. **JMnedict**: Download from [EDRDG](http://ftp.edrdg.org/pub/Nihongo/JMnedict.xml.gz) and extract.
5. **Sudachi Dictionary**: Download `system_full.dic` from the [SudachiDict releases](https://github.com/WorksApplications/SudachiDict/releases/latest).
6. **Kanji data** (optional, for kanji pages): download [KANJIDIC2](http://www.edrdg.org/kanjidic/kanjidic2.xml.gz) and extract it, and download `kanjivg-<date>-main.zip` from the [KanjiVG releases](https://github.com/KanjiVG/kanjivg/releases/latest) (keep it zipped).

You can download the FTP files using a variety of different tools, but you can do it with `curl` like so:
```sh
curl -O ftp://example.com/example.zip
```

## 2. Environment Setup

### Frontend Dependencies & HTTPS
```sh
cd Jiten.Web
pnpm install

# Generate dev certificates for local HTTPS
dotnet dev-certs https --export-path ./localhost.pem --format Pem --no-password
mv localhost.key localhost-key.pem
```

### Configuration
Copy the example settings and update the paths:
```sh
cp Shared/sharedsettings.example.json Shared/sharedsettings.json
```
Edit `Shared/sharedsettings.json`:
- Set `StaticFilesPath`: Absolute path to the `static` folder in the repo root.
- Set `DictionaryPath`: Absolute path to your `system_full.dic`.

## 3. Infrastructure (Databases)

We recommend you use Docker Compose. You can also run uncontainerized versions of all of them, but that is out of scope of this guide. To enable access from local services to the docker container, you need to modify the `docker-compose.yml` file:
```yml
services:
  postgres:
    # ...
    ports:
      - "5432:5432"
  # ...
  # This is if you want to use Umami
  umami:
    # ...
    ports:
      - "3005:3005"
  redis:
    # ...
    ports:
      - "6379:6379"
```

Start the services:
```sh
docker compose up postgres redis umami
```
You can run it with `-d` for it to be in the background.

## 4. Database Initialization & Import

While running the services from Step 3, run these commands from the repository root:

**Apply Migrations:**
```sh
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- --verbose --apply-migrations
```

**Import Dictionary Data:**
(Replace `/path/to/` with your actual file locations)
```sh
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- --verbose -i \
  --xml /path/to/jmdict_dtd.xml \
  --dic /path/to/JMdict \
  --namedic /path/to/JMnedict.xml \
  --furi /path/to/JmdictFurigana.json
```

**Import Kanji Data (optional):**
```sh
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- --import-kanjidic /path/to/kanjidic2.xml
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- --import-kanjivg /path/to/kanjivg-main.zip
```

**Create Admin User:**
```sh
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- --register-admin \
  --email admin@example.com \
  --username admin \
  --password yourpassword
```

## 5. Running the Application

### Start the Backend API
```sh
dotnet run --project Jiten.Api/Jiten.Api.csproj --urls "https://localhost:7299"
```

### Start the Frontend
In a new terminal:
```sh
cd Jiten.Web
pnpm dev
```

## Troubleshooting & Tips

### HTTPS Certificates
If you see a "Not Trusted" warning in your browser, you will need to add the `localhost.pem` generated in Step 2 to your OS/Browser trust store. Instructions will vary depending on your system.
