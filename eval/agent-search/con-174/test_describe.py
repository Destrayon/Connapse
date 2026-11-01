"""Offline checks for CON-174: the container prompt copies match the C# source, the proxy edits only what it should, and
the sample is fixed and leaves out the gold records.

    python -m pytest eval/agent-search/con-174/test_describe.py      (no stack, no model calls)
"""
import re
import sys
import textwrap
from pathlib import Path

import pytest

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE))
from container_summary import (CONTAINER_ROLLUP_SYSTEM_PROMPT, SAMPLE_SIZE,  # noqa: E402
                               render_container_rollup_user_message, sample_rows)
from describe_proxy import PLACEHOLDER, annotate_describe  # noqa: E402

SRC = Path(__file__).resolve().parents[3] / "src"
PROMPTS = SRC / "Connapse.Storage" / "Llm" / "SummaryPrompts.cs"
DESCRIBE = """Container: eval-erb-50k
ID: 0b1c
Kind: Managed
Type: managed storage (browsable and writable)
Summary: (not yet generated)
Documents: 50000
Storage: 61.2 MB
Created: 2026-10-01 00:00:00Z"""


def csharp_raw_string(name, source):
    m = re.search(re.escape(name) + r'\s*=?\s*\$?"""\r?\n(.*?)\r?\n([ \t]*)""";', source, re.S)
    return textwrap.dedent(m.group(1).replace("\r\n", "\n"))


def test_rollup_system_prompt_matches_source():
    assert CONTAINER_ROLLUP_SYSTEM_PROMPT == csharp_raw_string("ContainerRollupSystemPrompt", PROMPTS.read_text(encoding="utf-8"))


@pytest.mark.parametrize("clustered", [True, False])
def test_rollup_user_message_matches_source_template(clustered):
    source = PROMPTS.read_text(encoding="utf-8")
    method = source[source.index("RenderContainerRollupUserMessage"):]
    sample, all_shown = re.findall(r'\? "([^"]+)"\s*:\s*"([^"]+)"', method)[0]
    template = csharp_raw_string("return ", method)
    expected = (template.replace("{containerName}", "c").replace("{totalDocs}", "3")
                .replace("{clusterNote}", sample if clustered else all_shown).replace("{body}", "1. a\n2. b"))
    assert render_container_rollup_user_message("c", 3, clustered, ["a", "b"]) == expected


def test_clustered_prefix_matches_container_summarizer():
    summarizer = (SRC / "Connapse.Ingestion" / "Summarization" / "ContainerSummarizer.cs").read_text(encoding="utf-8")
    assert "(represents {" in summarizer and "similar docs): " in summarizer
    assert int(re.search(r"MaxClusters\s*=\s*(\d+)", summarizer).group(1)) == SAMPLE_SIZE


def test_placeholder_matches_mcp_tools():
    assert f'"{PLACEHOLDER}\\n"' in (SRC / "Connapse.Web" / "Mcp" / "McpTools.cs").read_text(encoding="utf-8")


def test_annotate_replaces_placeholder_only():
    out = annotate_describe(DESCRIBE, "  Use this container for X.\n- bullet  ")
    assert out == DESCRIBE.replace(PLACEHOLDER, "Summary: Use this container for X.\n- bullet")


def test_annotate_leaves_other_containers_alone():
    other = DESCRIBE.replace("eval-erb-50k", "other")
    assert annotate_describe(other, "s") == other


def test_annotate_refuses_a_stored_summary():
    with pytest.raises(ValueError):
        annotate_describe(DESCRIBE.replace(PLACEHOLDER, "Summary: stored by Connapse"), "s")


def test_sample_is_fixed_and_leaves_out_gold():
    excluded = set(range(0, 50_000, 7))
    a, b = sample_rows(50_000, excluded), sample_rows(50_000, excluded)
    assert a == b and len(a) == SAMPLE_SIZE and not excluded & set(a)
    assert sample_rows(50_000, set()) != sample_rows(50_000, set(), seed=175)
