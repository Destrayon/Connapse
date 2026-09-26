# pgvector/pgvector:pg17 plus Tiger Data's pg_textsearch (BM25, PostgreSQL License).
FROM pgvector/pgvector:pg17

ARG PG_TEXTSEARCH_VERSION=1.4.0
ARG TARGETARCH=amd64

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates unzip \
    && curl -fsSL "https://github.com/timescale/pg_textsearch/releases/download/v${PG_TEXTSEARCH_VERSION}/pg-textsearch-v${PG_TEXTSEARCH_VERSION}-pg17-${TARGETARCH}.zip" -o /tmp/pgts.zip \
    && unzip /tmp/pgts.zip -d /tmp/pgts \
    && apt-get install -y /tmp/pgts/*.deb \
    && rm -rf /tmp/pgts /tmp/pgts.zip \
    && apt-get purge -y curl unzip \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# pg_textsearch must be preloaded. Set in the sample config initdb copies, not in CMD, so it
# survives callers (Testcontainers, compose) that replace the command line.
RUN echo "shared_preload_libraries = 'pg_textsearch'" >> /usr/share/postgresql/postgresql.conf.sample

# The sample only reaches databases initialised from this image; the command line also covers a
# data volume first created by the stock pgvector image.
CMD ["postgres", "-c", "shared_preload_libraries=pg_textsearch"]
