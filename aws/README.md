# AWS credentials for S3 sources

Drop your AWS `credentials` and/or `config` file in this folder and restart Connapse:

    docker compose restart web

Connapse mounts this folder read-only at `~/.aws` inside the container and reads whatever it
finds. It stores no credential of its own — without something here (or an instance role, or
`CONNAPSE_AWS_DIR` pointing elsewhere in `.env`), every S3 connection fails with
"No AWS credentials found".

To use the profile you already have instead of copying it, set this in `.env`:

    CONNAPSE_AWS_DIR=C:\Users\you\.aws      # Windows
    CONNAPSE_AWS_DIR=/home/you/.aws         # Linux and macOS

Anything you put here is git-ignored. On EC2, ECS or EKS, attach an instance role and ignore
this folder entirely.
