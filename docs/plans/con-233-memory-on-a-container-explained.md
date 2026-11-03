# Why keep agent memory on a container?

A container is one project's shelf of documents. Memory on that container is a short list of facts that agents
learned while working on the project, kept next to the documents they came from. Without it, every agent session
starts from zero and has to dig the same answers out of the documents again, or ask the same person again.

Team memory is the shelf's shared notebook. Personal memory is your own sticky note on the shelf that nobody else
sees. Here is what each looks like day to day.

## Team memory: things the whole team should know

- **"Who do I ask?"** An agent finds out that Maria owns the billing service. It saves that for the team. Next
  week a new hire's agent answers "who owns billing?" straight away, with a link to the page it came from.
- **Decisions and why.** "We chose PostgreSQL over MongoDB in March, because of reporting needs (meeting notes,
  12 March)." Six months later nobody reopens the argument without knowing why it was settled.
- **Things that changed.** "The staging server moved to staging2 on 1 October." An agent asked about the old one
  says it moved and when, instead of sending someone to a dead link. The old value stays in history.
- **Gotchas.** "The import job fails on files over 2 GB; split them first." The first person to hit it saves an
  afternoon for everyone after.
- **Team rules that live nowhere else.** "No deploys on the last Friday of the quarter." It isn't written in any
  document, so search would never find it; team memory is where it lives.

## Personal memory: things only you need

- **How you like to work.** "On this project, show me the SQL behind every number" or "I only
  review the API docs here, not the UI ones."
- **What you're in the middle of.** "I'm halfway through rewriting the onboarding guide." Your agent picks up
  where you left off; your teammates' agents don't get confused by your unfinished work.

## Why on the container, and not somewhere global

- **The right people see it.** A fact about a private project stays with that project. Only people who can open
  the container, and every document the fact cites, see it.
- **It stays with its evidence.** Each fact points to the documents it came from, so you can check it in one
  click and fix or delete it on the same Memory tab where you browse those documents.
- **It ends when the project ends.** Delete the container and its memory goes with it, rather than lingering in
  everyone's agents.
