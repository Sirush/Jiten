# Credits
- [Sudachi.rs](https://github.com/WorksApplications/sudachi.rs) - Morphological analyzer
- [Nazeka](https://github.com/wareya/nazeka) - Original deconjugation rules, deconjugator
- [JL](https://github.com/rampaa/JL/tree/master) - Updated deconjugation rules, deconjugator port
- [Ichiran](https://github.com/tshatrov/ichiran) - Parser tests
- [JMDict](https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project) - Dictionary
- [JmdictFurigana](https://github.com/Doublevil/JmdictFurigana) - Furigana dictionary for JMDict
- [Lapis](https://github.com/donkuri/lapis) - Anki notetype
- [Hatsuon](https://github.com/DJTB/hatsuon) - Pitch accent display

# Installation

## System Requirements

- **Operating System:** Linux, macOS, or Windows (with Docker support)
- **CPU & Memory:** Minimum 2 CPU cores and 4GB of RAM recommended for development
- **Ports:** Ensure ports `8080`, `3001`, and `3005` are available for the API, web, and Umami services respectively

## Prerequisites

Before you begin, make sure you have the following installed on your machine:

- **Docker:** [Download and install Docker](https://docs.docker.com/get-docker/)
- **Docker Compose:** [Download and install Docker Compose](https://docs.docker.com/compose/install/)
- **Git:** To clone the repository ([Download Git](https://git-scm.com/downloads))

## Installation Steps - Frontend

### 1. Clone the Repository

Open your terminal and run the following command to clone the repository:

```bash
git clone https://github.com/Sirush/Jiten.git
cd Jiten
```

### 2. Configure Environment Variables

The project uses an environment file to set various configuration options for the services. Follow these steps:

1. Copy the provided `.env.example` file to a new file named `.env`:

   ```bash
   cp .env.example .env
   ```

2. Open the `.env` file in your favorite text editor and modify the variables as needed.

### 3. Start the Services

With Docker and Docker Compose installed and the environment variables configured, you can now start the application:

1. Open a terminal in the project root directory.
2. Run the following command to spin up the services:

   ```bash
   docker-compose up -d
   ```

   This command will:
   - Build and run the API and Web services using their respective Dockerfiles.
   - Pull and run the Postgres and Umami containers.
   - Create and attach the required volumes.

## Installation Steps - CLI

_coming soon™_

## Services Overview

- **Postgres:**
  - Database for the project.

- **API:**
  - Built from the `Jiten.Api/Dockerfile`.
  - Exposes port `8080`.

- **Web:**
  - Nuxt frontend.
  - Built from the `Jiten.Web/Dockerfile`.
  - Exposes port `3001`.

- **Umami:**
  - A web analytics tool based on Umami running with PostgreSQL.
  - Exposes port `3005`.

## Additional Notes

- **Traefik Labels:**  
  The services are configured with Traefik labels for reverse proxying. Make sure your Traefik setup is compatible if you plan to use it for routing (e.g., the rules `Host(api.jiten.moe)`, `Host(jiten.moe)`, and `Host(umami.jiten.moe)`).

- **Local Development:**  
  When developing locally, you might want to adjust URLs in the `.env` file to match your local environment (e.g., `API_BASE_URL=http://localhost:8080/api`).

- **Persistent Storage:**  
  The Docker Compose file defines persistent volumes (`postgres_data`, `uploads`, and `dictionaries`) to store data across container restarts.

# Parser performance & cache
Activating the cache can offer an appreciable speedup at the cost of RAM

Here's 3 scenarios, on 75 decks totaling 42millions moji, all running on 8 threads:

- Word Cache & Deconjugator cache: 316303ms / 8 GB RAM / 8m moji/min
- Word Cache only: 324542ms / 3.7 GB RAM / 7.8m moji/min
- No Cache: 502354ms / 3 GB RAM / 5m moji/min

The best option is to have the word cache only, the deconjugator only offering ~3% more speed at a great cost of RAM
