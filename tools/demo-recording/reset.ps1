# Empties the demo instance (database, files, app data) but keeps the downloaded Ollama model,
# then starts it again and waits until the web app answers. Run from the repository root.
$env:COMPOSE_PROJECT_NAME = 'connapse-demo'
$env:COMPOSE_FILE = 'docker-compose.yml;tools/demo-recording/compose.demo.yml'
docker compose --profile demo-source down
docker volume rm connapse-demo_pgdata connapse-demo_miniodata connapse-demo_appdata connapse-demo_secrets
if ($args -notcontains '-NoStart') {
    docker compose up -d
    $port = if ($env:DEMO_PORT) { $env:DEMO_PORT } else { '5201' }
    for ($i = 0; $i -lt 90; $i++) {
        try { Invoke-WebRequest "http://localhost:$port/register" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep 2 }
    }
}
