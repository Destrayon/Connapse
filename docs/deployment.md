# Deployment Guide

This guide covers deploying AIKnowledgePlatform in various environments.

## Table of Contents

- [Quick Start (Docker Compose)](#quick-start-docker-compose)
- [Local Development](#local-development)
- [Production Deployment](#production-deployment)
- [Cloud Deployments](#cloud-deployments)
- [Configuration Reference](#configuration-reference)
- [Backup and Restore](#backup-and-restore)
- [Troubleshooting](#troubleshooting)

---

## Quick Start (Docker Compose)

The fastest way to run AIKnowledgePlatform with all dependencies.

### Prerequisites

- **Docker** 24+ and **Docker Compose** 2.20+
- **Git** (to clone the repository)
- **8GB RAM** minimum (16GB recommended for Ollama)
- **20GB disk space** (for databases, models, and uploaded files)

### Steps

1. **Clone the repository**:
```bash
git clone https://github.com/yourorg/AIKnowledgePlatform.git
cd AIKnowledgePlatform
```

2. **Create environment file**:
```bash
cp .env.example .env
```

Edit `.env` and set your passwords:
```env
POSTGRES_PASSWORD=your_secure_password_here
MINIO_ROOT_USER=aikp_admin
MINIO_ROOT_PASSWORD=your_secure_minio_password_here
```

3. **Start services**:
```bash
# Without Ollama (use external embedding/LLM services)
docker compose up -d

# With Ollama (includes local embedding + LLM)
docker compose --profile with-ollama up -d
```

4. **Wait for services to be ready**:
```bash
docker compose ps
# All services should show "healthy" status
```

5. **Pull Ollama models** (if using Ollama):
```bash
docker compose exec ollama ollama pull nomic-embed-text
docker compose exec ollama ollama pull llama2  # Optional: for LLM features
```

6. **Initialize the database**:

The database schema is created automatically on first startup via EF Core migrations.

7. **Access the application**:

- **Web UI**: http://localhost:5001
- **MinIO Console**: http://localhost:9001 (login: `aikp_admin` / your password)
- **Ollama API**: http://localhost:11434

### Verify Installation

1. Open http://localhost:5001
2. Go to **Settings** → **Connection Testing**
3. Click "Test Connection" for PostgreSQL, MinIO, and Ollama
4. All tests should show ✅ Success

---

## Local Development

Run the application directly on your machine for development.

### Prerequisites

- **.NET 10 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/10.0))
- **PostgreSQL 17** with pgvector extension
- **MinIO** (or use Docker for just the dependencies)
- **Ollama** (optional, for local embeddings)

### Option 1: Hybrid (Docker Dependencies + .NET Runtime)

Run infrastructure in Docker, application in .NET runtime for hot reload and debugging.

1. **Start dependencies**:
```bash
docker compose up -d postgres minio ollama
```

2. **Configure connection strings**:
```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=aikp;Username=aikp;Password=aikp_dev" --project src/AIKnowledge.Web
dotnet user-secrets set "Knowledge:Storage:MinIO:Endpoint" "localhost:9000" --project src/AIKnowledge.Web
dotnet user-secrets set "Knowledge:Storage:MinIO:AccessKey" "aikp_dev" --project src/AIKnowledge.Web
dotnet user-secrets set "Knowledge:Storage:MinIO:SecretKey" "aikp_dev_secret" --project src/AIKnowledge.Web
```

3. **Run the application**:
```bash
dotnet run --project src/AIKnowledge.Web
```

4. **Open your browser**: https://localhost:5001

### Option 2: Fully Local (No Docker)

Install all dependencies on your machine.

#### PostgreSQL Setup

1. **Install PostgreSQL 17**:
   - **macOS**: `brew install postgresql@17`
   - **Ubuntu**: `sudo apt install postgresql-17`
   - **Windows**: [Download installer](https://www.postgresql.org/download/windows/)

2. **Install pgvector extension**:
```bash
# macOS/Linux
brew install pgvector  # or build from source
sudo apt install postgresql-17-pgvector  # Ubuntu

# Windows: Download from https://github.com/pgvector/pgvector/releases
```

3. **Create database**:
```bash
psql -U postgres
CREATE DATABASE aikp;
CREATE USER aikp WITH PASSWORD 'aikp_dev';
GRANT ALL PRIVILEGES ON DATABASE aikp TO aikp;
\c aikp
CREATE EXTENSION IF NOT EXISTS vector;
\q
```

#### MinIO Setup

1. **Install MinIO**:
```bash
# macOS
brew install minio

# Linux
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
sudo mv minio /usr/local/bin/

# Windows: Download from https://min.io/download
```

2. **Start MinIO**:
```bash
mkdir -p ~/minio/data
export MINIO_ROOT_USER=aikp_dev
export MINIO_ROOT_PASSWORD=aikp_dev_secret
minio server ~/minio/data --console-address ":9001"
```

#### Ollama Setup (Optional)

1. **Install Ollama**: https://ollama.ai/download

2. **Pull models**:
```bash
ollama pull nomic-embed-text
ollama pull llama2
```

3. **Verify**:
```bash
ollama list
```

#### Run Application

```bash
dotnet run --project src/AIKnowledge.Web
```

### Development Tools

**Recommended IDE**:
- Visual Studio 2022 (17.8+) with .NET 10 workload
- JetBrains Rider 2024.1+
- VS Code with C# Dev Kit

**Useful Commands**:
```bash
# Build
dotnet build

# Run tests
dotnet test

# Run specific test category
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"

# Watch mode (auto-rebuild on changes)
dotnet watch --project src/AIKnowledge.Web

# EF Core migrations
dotnet ef migrations add MigrationName --project src/AIKnowledge.Storage --startup-project src/AIKnowledge.Web
dotnet ef database update --project src/AIKnowledge.Storage --startup-project src/AIKnowledge.Web

# Format code
dotnet format
```

---

## Production Deployment

### Security Hardening

#### 1. Secrets Management

**NEVER commit secrets to git**. Use environment variables or secret managers.

**Azure Key Vault**:
```bash
# Install provider
dotnet add package Azure.Extensions.AspNetCore.Configuration.Secrets

# Configure in Program.cs
builder.Configuration.AddAzureKeyVault(
    new Uri("https://your-keyvault.vault.azure.net/"),
    new DefaultAzureCredential());
```

**AWS Secrets Manager**:
```bash
dotnet add package Amazon.Extensions.Configuration.SystemsManager

builder.Configuration.AddSystemsManager("/aikp/production");
```

**Environment Variables** (Kubernetes, Docker):
```yaml
# Kubernetes Secret
apiVersion: v1
kind: Secret
metadata:
  name: aikp-secrets
type: Opaque
data:
  postgres-password: <base64-encoded-password>
  minio-secret-key: <base64-encoded-key>
```

#### 2. HTTPS/TLS

**Option A: Reverse Proxy (Recommended)**

Use nginx or Traefik with Let's Encrypt:

```nginx
# /etc/nginx/sites-available/aikp
server {
    listen 443 ssl http2;
    server_name aikp.example.com;

    ssl_certificate /etc/letsencrypt/live/aikp.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/aikp.example.com/privkey.pem;

    location / {
        proxy_pass http://localhost:5001;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # WebSocket support (SignalR)
    location /hubs/ {
        proxy_pass http://localhost:5001;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
    }
}
```

**Option B: Kestrel Direct HTTPS**

```bash
# Generate self-signed cert (dev only)
dotnet dev-certs https -ep ./cert.pfx -p YourPassword

# Production: Use real certificate
export ASPNETCORE_Kestrel__Certificates__Default__Path=/path/to/cert.pfx
export ASPNETCORE_Kestrel__Certificates__Default__Password=YourPassword
export ASPNETCORE_URLS="https://+:443;http://+:80"
```

#### 3. Database Security

```sql
-- Create read-only user for analytics
CREATE ROLE aikp_readonly;
GRANT CONNECT ON DATABASE aikp TO aikp_readonly;
GRANT USAGE ON SCHEMA public TO aikp_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO aikp_readonly;

-- Create app user with limited permissions
CREATE USER aikp_app WITH PASSWORD 'secure_password';
GRANT CONNECT ON DATABASE aikp TO aikp_app;
GRANT USAGE, CREATE ON SCHEMA public TO aikp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO aikp_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO aikp_app;

-- Enable SSL
ALTER SYSTEM SET ssl = on;
SELECT pg_reload_conf();
```

**Connection string with SSL**:
```
Host=postgres.example.com;Database=aikp;Username=aikp_app;Password=***;SSL Mode=Require;Trust Server Certificate=false
```

#### 4. MinIO Security

```bash
# Create dedicated user (not root)
mc admin user add myminio aikp_app <secure-password>

# Create policy with minimal permissions
cat > /tmp/aikp-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::knowledge-files/*",
        "arn:aws:s3:::knowledge-files"
      ]
    }
  ]
}
EOF

mc admin policy create myminio aikp-policy /tmp/aikp-policy.json
mc admin policy attach myminio aikp-policy --user aikp_app
```

### Dockerfile Optimization

**Multi-stage build** for minimal image size:

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["src/AIKnowledge.Web/AIKnowledge.Web.csproj", "AIKnowledge.Web/"]
COPY ["src/AIKnowledge.Core/AIKnowledge.Core.csproj", "AIKnowledge.Core/"]
COPY ["src/AIKnowledge.Ingestion/AIKnowledge.Ingestion.csproj", "AIKnowledge.Ingestion/"]
COPY ["src/AIKnowledge.Search/AIKnowledge.Search.csproj", "AIKnowledge.Search/"]
COPY ["src/AIKnowledge.Storage/AIKnowledge.Storage.csproj", "AIKnowledge.Storage/"]

# Restore dependencies
RUN dotnet restore "AIKnowledge.Web/AIKnowledge.Web.csproj"

# Copy everything else
COPY src/ .

# Build and publish
WORKDIR "/src/AIKnowledge.Web"
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r aikp && useradd -r -g aikp aikp
RUN chown -R aikp:aikp /app
USER aikp

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "AIKnowledge.Web.dll"]
```

**Build and push**:
```bash
docker build -t yourregistry/aikp:v1.0.0 .
docker push yourregistry/aikp:v1.0.0
```

### Docker Compose (Production)

```yaml
# docker-compose.prod.yml
services:
  postgres:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: aikp
      POSTGRES_USER: aikp_app
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
    secrets:
      - postgres_password
    volumes:
      - pgdata:/var/lib/postgresql/data
    networks:
      - backend
    restart: unless-stopped

  minio:
    image: minio/minio
    command: server /data --console-address ":9001"
    environment:
      MINIO_ROOT_USER_FILE: /run/secrets/minio_user
      MINIO_ROOT_PASSWORD_FILE: /run/secrets/minio_password
    secrets:
      - minio_user
      - minio_password
    volumes:
      - miniodata:/data
    networks:
      - backend
    restart: unless-stopped

  web:
    image: yourregistry/aikp:v1.0.0
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=aikp;Username=aikp_app;Password_FILE=/run/secrets/postgres_password;SSL Mode=Require"
      Knowledge__Storage__MinIO__Endpoint: "minio:9000"
      Knowledge__Storage__MinIO__AccessKey_FILE: /run/secrets/minio_user
      Knowledge__Storage__MinIO__SecretKey_FILE: /run/secrets/minio_password
    secrets:
      - postgres_password
      - minio_user
      - minio_password
    networks:
      - frontend
      - backend
    depends_on:
      - postgres
      - minio
    restart: unless-stopped

  nginx:
    image: nginx:alpine
    ports:
      - "443:443"
      - "80:80"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./certs:/etc/nginx/certs:ro
    networks:
      - frontend
    depends_on:
      - web
    restart: unless-stopped

secrets:
  postgres_password:
    file: ./secrets/postgres_password.txt
  minio_user:
    file: ./secrets/minio_user.txt
  minio_password:
    file: ./secrets/minio_password.txt

networks:
  frontend:
  backend:

volumes:
  pgdata:
  miniodata:
```

---

## Cloud Deployments

### Azure (Container Apps + Managed Services)

#### Architecture

```
[Azure Front Door]
    ↓
[Container App (AIKnowledge.Web)]
    ↓
[Azure Database for PostgreSQL Flexible Server + pgvector]
[Azure Blob Storage (MinIO replacement)]
[Azure OpenAI / Ollama in Azure Container Instances]
```

#### Deployment Steps

1. **Create Resource Group**:
```bash
az group create --name aikp-prod --location eastus
```

2. **Provision PostgreSQL**:
```bash
az postgres flexible-server create \
  --resource-group aikp-prod \
  --name aikp-postgres \
  --location eastus \
  --admin-user aikpadmin \
  --admin-password <SecurePassword> \
  --sku-name Standard_D2s_v3 \
  --version 17

# Enable pgvector extension
az postgres flexible-server parameter set \
  --resource-group aikp-prod \
  --server-name aikp-postgres \
  --name azure.extensions --value VECTOR
```

3. **Create Storage Account**:
```bash
az storage account create \
  --name aikpstorage \
  --resource-group aikp-prod \
  --location eastus \
  --sku Standard_LRS

az storage container create \
  --name knowledge-files \
  --account-name aikpstorage
```

4. **Deploy Container App**:
```bash
az containerapp create \
  --name aikp-web \
  --resource-group aikp-prod \
  --image yourregistry/aikp:v1.0.0 \
  --environment aikp-env \
  --target-port 8080 \
  --ingress external \
  --min-replicas 2 \
  --max-replicas 10 \
  --secrets \
    postgres-password=<SecurePassword> \
    storage-key=<StorageAccountKey> \
  --env-vars \
    "ASPNETCORE_ENVIRONMENT=Production" \
    "ConnectionStrings__DefaultConnection=secretref:postgres-password" \
    "Knowledge__Storage__BlobStorage__ConnectionString=secretref:storage-key"
```

#### Cost Estimate (Monthly)

- **Container Apps**: $50-200 (2-10 instances)
- **PostgreSQL Flexible Server (D2s_v3)**: $120
- **Blob Storage**: $5-50 (depending on usage)
- **Azure OpenAI**: $0.0001 per 1K tokens (~$10-100/month)

**Total**: ~$185-470/month

### AWS (ECS + Managed Services)

#### Architecture

```
[Application Load Balancer]
    ↓
[ECS Fargate (AIKnowledge.Web)]
    ↓
[RDS PostgreSQL 17 + pgvector]
[S3 (native, no MinIO)]
[Bedrock / Ollama in ECS]
```

#### Deployment (Terraform)

```hcl
# main.tf
resource "aws_ecs_cluster" "aikp" {
  name = "aikp-cluster"
}

resource "aws_db_instance" "postgres" {
  identifier           = "aikp-postgres"
  engine              = "postgres"
  engine_version      = "17.0"
  instance_class      = "db.t4g.medium"
  allocated_storage   = 100
  db_name             = "aikp"
  username            = "aikpadmin"
  password            = var.db_password
  publicly_accessible = false
  vpc_security_group_ids = [aws_security_group.db.id]

  # Enable pgvector via RDS Parameter Group
  parameter_group_name = aws_db_parameter_group.postgres17_pgvector.name
}

resource "aws_s3_bucket" "documents" {
  bucket = "aikp-documents-${var.environment}"
}

resource "aws_ecs_service" "aikp_web" {
  name            = "aikp-web"
  cluster         = aws_ecs_cluster.aikp.id
  task_definition = aws_ecs_task_definition.aikp_web.arn
  desired_count   = 2
  launch_type     = "FARGATE"

  load_balancer {
    target_group_arn = aws_lb_target_group.aikp_web.arn
    container_name   = "web"
    container_port   = 8080
  }
}
```

**Deploy**:
```bash
terraform init
terraform plan -var="db_password=SecurePassword"
terraform apply
```

#### Cost Estimate (Monthly)

- **ECS Fargate (2 tasks, 1 vCPU, 2GB)**: $60
- **RDS PostgreSQL (db.t4g.medium)**: $80
- **S3 Standard**: $5-50
- **Application Load Balancer**: $20
- **Data Transfer**: $10-50

**Total**: ~$175-260/month

### Google Cloud (Cloud Run + Managed Services)

#### Architecture

```
[Cloud Load Balancing]
    ↓
[Cloud Run (AIKnowledge.Web)]
    ↓
[Cloud SQL PostgreSQL 17 + pgvector]
[Cloud Storage (S3-compatible via XML API)]
[Vertex AI Embeddings]
```

#### Deployment

```bash
# Build and push image
gcloud builds submit --tag gcr.io/your-project/aikp:v1.0.0

# Create Cloud SQL instance
gcloud sql instances create aikp-postgres \
  --database-version=POSTGRES_17 \
  --tier=db-g1-small \
  --region=us-central1

# Enable pgvector
gcloud sql instances patch aikp-postgres \
  --database-flags=cloudsql.enable_pgvector=on

# Create storage bucket
gsutil mb gs://aikp-documents/

# Deploy to Cloud Run
gcloud run deploy aikp-web \
  --image gcr.io/your-project/aikp:v1.0.0 \
  --region us-central1 \
  --platform managed \
  --allow-unauthenticated \
  --min-instances 1 \
  --max-instances 10 \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production" \
  --set-secrets "ConnectionStrings__DefaultConnection=postgres-connection:latest"
```

#### Cost Estimate (Monthly)

- **Cloud Run**: $30-100 (1-10 instances)
- **Cloud SQL (db-g1-small)**: $50
- **Cloud Storage**: $5-50
- **Vertex AI Embeddings**: $0.00005 per 1K characters (~$10-50/month)

**Total**: ~$95-250/month

---

## Configuration Reference

### Environment Variables

All settings can be overridden via environment variables using the hierarchy:

```
appsettings.json < appsettings.{Env}.json < User Secrets < Environment Variables < CLI Args
```

#### Format

Use double underscores `__` to represent nested keys:

```bash
# appsettings.json:
# {
#   "Knowledge": {
#     "Embedding": {
#       "BaseUrl": "http://ollama:11434"
#     }
#   }
# }

# Equivalent environment variable:
export Knowledge__Embedding__BaseUrl="http://ollama:11434"
```

#### Core Settings

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Environment (Development, Staging, Production) | `Production` |
| `ASPNETCORE_URLS` | Bind URLs | `http://+:8080` |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | (required) |
| `Knowledge__Storage__MinIO__Endpoint` | MinIO endpoint | `minio:9000` |
| `Knowledge__Storage__MinIO__AccessKey` | MinIO access key | (required) |
| `Knowledge__Storage__MinIO__SecretKey` | MinIO secret key | (required) |
| `Knowledge__Storage__MinIO__UseSSL` | Use HTTPS for MinIO | `false` |
| `Knowledge__Storage__MinIO__BucketName` | MinIO bucket name | `knowledge-files` |
| `Knowledge__Embedding__BaseUrl` | Ollama/OpenAI base URL | `http://ollama:11434` |
| `Knowledge__Embedding__Model` | Embedding model name | `nomic-embed-text` |
| `Knowledge__Embedding__Dimensions` | Expected vector dimensions | `768` |
| `Knowledge__Chunking__Strategy` | Default chunking strategy | `Semantic` |
| `Knowledge__Chunking__MaxTokens` | Max tokens per chunk | `512` |
| `Knowledge__Chunking__Overlap` | Overlap tokens between chunks | `50` |
| `Knowledge__Search__Mode` | Default search mode | `Hybrid` |
| `Knowledge__Search__TopK` | Default result count | `10` |
| `Knowledge__Search__MinScore` | Minimum similarity score | `0.7` |
| `Knowledge__Upload__MaxFileSizeBytes` | Max upload size | `104857600` (100MB) |
| `Knowledge__Upload__ConcurrentIngestions` | Parallel ingestion workers | `4` |

### appsettings.json Structure

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Npgsql": "Warning"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=aikp;Username=aikp;Password=***"
  },
  "Knowledge": {
    "Embedding": {
      "Provider": "Ollama",
      "BaseUrl": "http://ollama:11434",
      "Model": "nomic-embed-text",
      "Dimensions": 768,
      "Timeout": 30,
      "BatchSize": 4
    },
    "Chunking": {
      "Strategy": "Semantic",
      "MaxTokens": 512,
      "Overlap": 50,
      "SimilarityThreshold": 0.8
    },
    "Search": {
      "Mode": "Hybrid",
      "TopK": 10,
      "MinScore": 0.7,
      "RerankerStrategy": "RRF",
      "RrfK": 60
    },
    "Llm": {
      "Provider": "Ollama",
      "BaseUrl": "http://ollama:11434",
      "Model": "llama2",
      "Temperature": 0.7,
      "MaxTokens": 2048
    },
    "Storage": {
      "MinIO": {
        "Endpoint": "minio:9000",
        "AccessKey": "aikp_dev",
        "SecretKey": "aikp_dev_secret",
        "UseSSL": false,
        "BucketName": "knowledge-files"
      }
    },
    "Upload": {
      "MaxFileSizeBytes": 104857600,
      "AllowedExtensions": [".txt", ".md", ".pdf", ".docx", ".pptx", ".csv", ".json", ".xml", ".yaml"],
      "ConcurrentIngestions": 4,
      "QueueCapacity": 1000
    },
    "WebSearch": {
      "Provider": "None",
      "ApiKey": "",
      "MaxResults": 10
    }
  }
}
```

---

## Backup and Restore

### PostgreSQL Backup

**Manual Backup**:
```bash
# Dump entire database
docker compose exec -T postgres pg_dump -U aikp aikp > backup_$(date +%Y%m%d_%H%M%S).sql

# Dump specific tables
docker compose exec -T postgres pg_dump -U aikp aikp -t documents -t chunks > backup_tables.sql

# Custom format (compressed)
docker compose exec -T postgres pg_dump -U aikp -Fc aikp > backup.dump
```

**Automated Backup** (cron):
```bash
# /etc/cron.d/aikp-backup
0 2 * * * docker compose -f /opt/aikp/docker-compose.yml exec -T postgres pg_dump -U aikp -Fc aikp > /backups/aikp_$(date +\%Y\%m\%d).dump
```

**Restore**:
```bash
# From SQL file
docker compose exec -T postgres psql -U aikp aikp < backup.sql

# From custom format
docker compose exec -T postgres pg_restore -U aikp -d aikp backup.dump
```

### MinIO Backup

**Using mc (MinIO Client)**:
```bash
# Install mc
brew install minio/stable/mc  # macOS
# Or download from https://min.io/docs/minio/linux/reference/minio-mc.html

# Configure alias
mc alias set myminio http://localhost:9000 aikp_dev aikp_dev_secret

# Mirror to local directory
mc mirror myminio/knowledge-files /backups/minio/knowledge-files

# Mirror to S3
mc mirror myminio/knowledge-files s3/my-backup-bucket/aikp-minio/
```

**Automated Backup** (cron):
```bash
# /etc/cron.d/aikp-minio-backup
0 3 * * * mc mirror myminio/knowledge-files /backups/minio/knowledge-files
```

### Full System Backup

```bash
#!/bin/bash
# backup.sh

BACKUP_DIR="/backups/aikp/$(date +%Y%m%d_%H%M%S)"
mkdir -p "$BACKUP_DIR"

# Backup PostgreSQL
docker compose exec -T postgres pg_dump -U aikp -Fc aikp > "$BACKUP_DIR/postgres.dump"

# Backup MinIO (incremental)
mc mirror myminio/knowledge-files "$BACKUP_DIR/minio"

# Backup settings and compose files
cp .env "$BACKUP_DIR/"
cp docker-compose.yml "$BACKUP_DIR/"

# Create tarball
tar czf "$BACKUP_DIR.tar.gz" "$BACKUP_DIR"
rm -rf "$BACKUP_DIR"

echo "Backup completed: $BACKUP_DIR.tar.gz"
```

### Disaster Recovery

1. **Deploy fresh infrastructure** (Docker Compose or cloud)
2. **Restore PostgreSQL**:
```bash
docker compose exec -T postgres pg_restore -U aikp -d aikp < postgres.dump
```
3. **Restore MinIO files**:
```bash
mc mirror /backups/minio/knowledge-files myminio/knowledge-files
```
4. **Restart services**:
```bash
docker compose restart
```

---

## Troubleshooting

### Common Issues

#### 1. "Connection to PostgreSQL failed"

**Symptoms**: Application won't start, logs show `Npgsql connection error`

**Solutions**:
```bash
# Check if Postgres is running
docker compose ps postgres

# Check logs
docker compose logs postgres

# Test connection
docker compose exec postgres psql -U aikp -d aikp -c "SELECT 1;"

# Verify connection string
docker compose exec web env | grep ConnectionStrings
```

#### 2. "MinIO bucket not found"

**Symptoms**: Upload fails with `The specified bucket does not exist`

**Solutions**:
```bash
# Create bucket
mc alias set local http://localhost:9000 aikp_dev aikp_dev_secret
mc mb local/knowledge-files

# Or via Docker
docker compose exec minio mc mb /data/knowledge-files
```

#### 3. "Ollama model not found"

**Symptoms**: Ingestion fails during embedding phase

**Solutions**:
```bash
# List available models
docker compose exec ollama ollama list

# Pull missing model
docker compose exec ollama ollama pull nomic-embed-text

# Verify model works
curl http://localhost:11434/api/embeddings -d '{
  "model": "nomic-embed-text",
  "prompt": "test"
}'
```

#### 4. "Ingestion queue full (429)"

**Symptoms**: Upload returns 429 Too Many Requests

**Solutions**:
- Wait for current jobs to complete
- Increase queue capacity:
```json
{
  "Knowledge": {
    "Upload": {
      "QueueCapacity": 2000,
      "ConcurrentIngestions": 8
    }
  }
}
```

#### 5. "Out of memory during large file upload"

**Symptoms**: Application crashes or becomes unresponsive

**Solutions**:
- Increase Docker memory limit:
```yaml
# docker-compose.yml
services:
  web:
    deploy:
      resources:
        limits:
          memory: 4G
```
- Reduce concurrent ingestions
- Increase chunking (smaller chunks = less memory per operation)

### Debug Mode

Enable detailed logging:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "AIKnowledge": "Trace",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

### Health Checks

Check service health:

```bash
# PostgreSQL
docker compose exec postgres pg_isready -U aikp

# MinIO
curl http://localhost:9000/minio/health/live

# Ollama
curl http://localhost:11434/api/tags

# Application (once implemented)
curl https://localhost:5001/health
```

### Performance Tuning

#### PostgreSQL

```sql
-- Increase shared buffers (25% of RAM)
ALTER SYSTEM SET shared_buffers = '2GB';

-- Increase work_mem for complex queries
ALTER SYSTEM SET work_mem = '64MB';

-- Enable parallel queries
ALTER SYSTEM SET max_parallel_workers_per_gather = 4;

-- Optimize for SSD
ALTER SYSTEM SET random_page_cost = 1.1;

-- Reload config
SELECT pg_reload_conf();
```

#### pgvector Index Tuning

```sql
-- HNSW index (faster search, more memory)
CREATE INDEX ON chunk_vectors USING hnsw (embedding vector_cosine_ops)
WITH (m = 16, ef_construction = 64);

-- IVFFLAT index (less memory, more setup time)
CREATE INDEX ON chunk_vectors USING ivfflat (embedding vector_cosine_ops)
WITH (lists = 100);
```

---

## Monitoring (Planned)

### Metrics

Prometheus + Grafana dashboard:

```yaml
# docker-compose.monitoring.yml
services:
  prometheus:
    image: prom/prometheus
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
      - promdata:/prometheus
    ports:
      - "9090:9090"

  grafana:
    image: grafana/grafana
    ports:
      - "3000:3000"
    environment:
      GF_SECURITY_ADMIN_PASSWORD: admin
    volumes:
      - grafana-data:/var/lib/grafana

volumes:
  promdata:
  grafana-data:
```

### Key Metrics

- **Ingestion**: docs/min, avg latency, queue depth
- **Search**: queries/sec, p50/p95/p99 latency, cache hit rate
- **Storage**: DB size, MinIO storage used, vector index size
- **System**: CPU, memory, disk I/O, network

---

## References

- [architecture.md](architecture.md) — System architecture
- [api.md](api.md) — API reference
- [Docker Compose Docs](https://docs.docker.com/compose/)
- [PostgreSQL Docs](https://www.postgresql.org/docs/)
- [pgvector GitHub](https://github.com/pgvector/pgvector)
- [MinIO Docs](https://min.io/docs/)
