"""Prints one work session's log for the note writer, so it sees a project's history in order (CON-38).

    python -I next_session.py <project-code> <index>

Prints the session's date and log, and how many sessions the project has. The note writer asks for
index 0, 1, 2, ... and writes each note before asking for the next, never reading sessions.jsonl itself.
"""
import json
import sys
from pathlib import Path

code, index = sys.argv[1], int(sys.argv[2])
path = Path(__file__).resolve().parent / "../../datasets/memory-questions/sessions.jsonl"
sessions = [s for s in map(json.loads, path.read_text(encoding="utf-8").splitlines()) if s["code"] == code]
s = sessions[index]
print(f"session {index + 1} of {len(sessions)} | project {s['project']} | date {s['date']} | id {s['session']}\n{s['log']}")
