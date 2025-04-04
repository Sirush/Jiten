ARG PG_VERSION=17
FROM postgres:${PG_VERSION}-bookworm

ENV CODENAME=bookworm

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        wget \
        ca-certificates \
        && wget https://packages.groonga.org/debian/groonga-apt-source-latest-${CODENAME}.deb \
        && apt-get install -y --no-install-recommends ./groonga-apt-source-latest-${CODENAME}.deb \
        && rm groonga-apt-source-latest-${CODENAME}.deb \
        && apt-get update \
        && apt-get install -y -V postgresql-17-pgdg-pgroonga \
        && apt-get clean \
        && rm -rf /var/lib/apt/lists/*

RUN apt-get update && apt-get install -y -V groonga-tokenizer-mecab && apt-get clean && rm -rf /var/lib/apt/lists/*