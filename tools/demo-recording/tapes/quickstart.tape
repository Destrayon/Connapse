# Shot 2, terminal half. Run from the raw-video folder with DEMO_REPO set to the repository root,
# after reset.ps1 -NoStart, so `up` starts an empty instance.
Output "quickstart-terminal.mp4"
Set Shell "powershell"
Set FontSize 22
Set Width 1280
Set Height 720
Set Padding 28
Set Theme "Dracula"
Set TypingSpeed 45ms
Set WaitTimeout 180s
Set WaitPattern /connapse>\s*$/

Hide
Type "$env:COMPOSE_PROJECT_NAME='connapse-demo'; $env:COMPOSE_FILE='docker-compose.yml;tools/demo-recording/compose.demo.yml'; cd $env:DEMO_REPO; function prompt { 'connapse> ' }; cls"
Enter
Sleep 2s
Show

Sleep 600ms
Type "docker compose --profile with-ollama up -d"
Sleep 400ms
Enter
Wait
Sleep 1500ms
Type "docker compose exec ollama ollama pull nomic-embed-text"
Sleep 400ms
Enter
Wait
Sleep 2s
