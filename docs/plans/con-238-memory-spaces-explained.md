# Memory spaces, in plain words

Agent memory is a short list of facts your AI agents learned while working, so the next session starts from what
the last one found instead of from zero. Facts live in **memory spaces**, which you make on the **Memory** page in
the nav. Think of a space as a notebook. Each notebook is either yours alone or the team's, and you decide which
when you make it.

## Personal spaces: your own notebook

Only you and the agents acting for you can see a personal space, or even tell that it exists. Every Editor has one
called `my-memory` from the start, and you can make more.

- **How you like to work.** "Show me the SQL behind every number" or "I only review the API docs, not the UI ones."
- **What you're in the middle of.** "I'm halfway through rewriting the onboarding guide." Your agent picks up where
  you left off, and your teammates' agents aren't confused by your unfinished work.

## Team spaces: the team's shared notebook

Everyone with an account can search a team space, Viewers included. Editors and admins can add to it. It's shared
the moment it's made; there's nobody to invite.

- **"Who do I ask?"** An agent finds out that Maria owns the billing service and saves it to the `on-call` space. Next
  week a new hire's agent answers "who owns billing?" straight away.
- **Team rules that live nowhere else.** "No deploys on the last Friday of the quarter." It isn't written in any
  document, so search would never find it; a team space is where it lives.
- **Gotchas.** "The import job fails on files over 2 GB; split them first." The first person to hit it saves an
  afternoon for everyone after.

## Memory on a document container: notes on a project's shelf

A container is one project's shelf of documents. Its Memory tab holds team facts learned from those documents, kept
next to them so you can check each one in a click.

- **Decisions and why.** "We chose PostgreSQL over MongoDB in March, because of reporting needs (meeting notes,
  12 March)." Six months later nobody reopens the argument without knowing why it was settled.
- **Things that changed.** "The staging server moved to staging2 on 1 October." An agent asked about the old one
  says it moved and when, instead of sending someone to a dead link.

Memory on a container is always team memory. Personal notes about a project go in a personal space.

## Sharing a personal note with the team

If something in your personal space turns out to be useful to everyone, press **Copy to team** next to it and pick
a team space or container. Only the note as it stands now is copied, with its dates and the documents it cites; its
earlier versions stay private. Your personal note stays where it was, and changing one later doesn't change the
other. Viewers can't copy, because copying is saving.

## Two rules that hold everywhere

- **A fact never shows more than its documents.** If a fact cites a document, only people who can read that
  document see the fact, even in a team space.
- **Saving needs the Editor role.** Every saved fact costs storage and compute, so Viewers and read-only keys can
  search team memory but can't add to it.

## What happens to memory you already have

Nobody sees anything new and nobody loses anything. Facts only you could see move to a personal space named after
their container, such as `halcyon-project-personal`. Agent memory containers become team spaces with the same
name, so agents that use them keep working.
