"""Checks that summarize.py's copies of Connapse's per-document prompt and template still match the C# source.

    python -m pytest eval/agent-search/con-160/test_summarize.py      (or run this file directly)
"""
import re
import textwrap
from pathlib import Path

from summarize import MAX_INPUT_CHARS, PER_DOC_SYSTEM_PROMPT, render_per_doc_user_message

SOURCE = Path(__file__).resolve().parents[3] / "src" / "Connapse.Storage" / "Llm" / "SummaryPrompts.cs"


def csharp_raw_string(name):
    """The value of a C# raw string literal `name = \"\"\" ... \"\"\";`, with its indentation removed like C# does."""
    m = re.search(re.escape(name) + r'\s*=?\s*\$?"""\r?\n(.*?)\r?\n([ \t]*)""";', SOURCE.read_text(encoding="utf-8"), re.S)
    return textwrap.dedent(m.group(1).replace("\r\n", "\n"))


def test_per_doc_system_prompt_matches_source():
    assert PER_DOC_SYSTEM_PROMPT == csharp_raw_string("PerDocSystemPrompt")


def test_per_doc_user_message_matches_source_template():
    source = SOURCE.read_text(encoding="utf-8")
    for note in re.findall(r'\? "([^"]+)"\s*:\s*"([^"]+)"', source)[0]:
        assert note in render_per_doc_user_message("a.txt", "text/plain", "x", True) + \
            render_per_doc_user_message("a.txt", "text/plain", "x", False)
    template = csharp_raw_string("return ")
    rendered = render_per_doc_user_message("{filename}", "text/plain", "{firstNTokens}", False)
    expected = (template.replace('{mimeType ?? "unknown"}', "text/plain")
                .replace("{coverageNote}", "; the full document text is shown below"))
    assert rendered == expected


def test_truncation_matches_per_doc_summarizer():
    summarizer = (SOURCE.parents[2] / "Connapse.Ingestion" / "Summarization" / "PerDocSummarizer.cs").read_text(encoding="utf-8")
    tokens = int(re.search(r"DefaultMaxInputTokens\s*=\s*([\d_]+)", summarizer).group(1).replace("_", ""))
    assert MAX_INPUT_CHARS == tokens * 4


if __name__ == "__main__":
    for test in [v for k, v in dict(globals()).items() if k.startswith("test_")]:
        test()
        print("ok", test.__name__)
