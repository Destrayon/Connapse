"""Tests for pipeline_time.py (CON-223). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py".

The replays are real: fixtures/pipeline-time-replays.json.gz holds, for the four latest runs that failed the
old wall-time check with no slower tests, each run's passed tests and durations by job, and those of the
five main runs that were its baseline, newest first.
"""
import contextlib
import gzip
import io
import json
import os
import pathlib
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import pipeline_time as pt  # noqa: E402

FIXTURE = pathlib.Path(__file__).parent / "fixtures" / "pipeline-time-replays.json.gz"
LIMIT = 45  # ci.yml's --max-added-seconds
T0 = datetime(2026, 11, 2, 22, 0, 0, tzinfo=timezone.utc)


def load() -> tuple[dict[str, dict[str, dict[str, float]]], list[dict]]:
    with gzip.open(FIXTURE, "rt", encoding="utf-8") as f:
        data = json.load(f)
    names = data["names"]
    runs = {rid: {job: {names[i]: s for i, s in rows} for job, rows in jobs.items()} for rid, jobs in data["runs"].items()}
    return runs, data["replays"]


RUNS, REPLAYS = load()


def replay(rid: str) -> tuple[dict, list[dict]]:
    r = next(r for r in REPLAYS if r["run"] == rid)
    return json.loads(json.dumps(RUNS[rid])), [RUNS[b] for b in r["baseline"]]


def iso(seconds: float) -> str:
    return (T0 + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")


def job(name: str, seconds: float) -> dict:
    return {"name": name, "conclusion": "success", "started_at": iso(0), "completed_at": iso(seconds)}


def main(run: dict, baseline: list[dict], longest_job: float = 300, own: float = 20) -> tuple[int, str]:
    """pipeline_time.main on these results, with GitHub mocked: the longest job ran this long and this
    job has run this long."""
    js = [job("integration (2)", longest_job), job("small", 100),
          {"name": "build-and-test", "started_at": iso(longest_job), "completed_at": None}]
    out = io.StringIO()
    with mock.patch.object(pt, "results", return_value=run), \
         mock.patch.object(pt, "baseline_runs", return_value=[(str(i), b, 340.0 + i) for i, b in enumerate(baseline)]), \
         mock.patch.object(pt, "jobs", return_value=js), \
         mock.patch.object(pt, "utcnow", return_value=T0 + timedelta(seconds=longest_job + own)), \
         mock.patch.dict(os.environ, {"GITHUB_RUN_ID": "1", "GITHUB_STEP_SUMMARY": ""}), \
         contextlib.redirect_stdout(out):
        rc = pt.main(["--results", ".", "--max-added-seconds", str(LIMIT), "--max-wall-seconds", "360",
                      "--current", "build-and-test"])
    return rc, out.getvalue()


class Replays(unittest.TestCase):
    """The bar, set before the replay: every run that failed only on wall time passes."""

    def test_added_RunsThatFailedOnlyOnWallTime_StayWellUnderTheLimit(self):
        self.assertEqual({r["run"] for r in REPLAYS}, {"41521110441", "41535048861", "41538112206", "41567881057"})
        for r in REPLAYS:
            with self.subTest(run=r["run"], branch=r["branch"]):
                run, baseline = replay(r["run"])
                more, _, _, _ = pt.added(run, baseline)
                self.assertLess(more, 10)

    def test_main_Pr1103AtItsFailedWallTime_PassesWithAWarning(self):
        # 41535048861's first attempt: 446 s of wall, from an 85 s close of integration shard 2.
        run, baseline = replay("41535048861")
        rc, out = main(run, baseline, longest_job=426, own=20)
        self.assertEqual(rc, 0, out)
        self.assertIn("::warning::pipeline wall time 446 s of 360 s", out)
        self.assertNotIn("::error::", out)

    def test_main_EveryReplayedRun_Passes(self):
        for r in REPLAYS:
            with self.subTest(run=r["run"]):
                rc, out = main(*replay(r["run"]))
                self.assertEqual(rc, 0, out)


class SyntheticSlowPr(unittest.TestCase):
    """A PR that really adds a minute of tests still fails, on the same runners."""

    def test_main_TwelveNewFiveSecondTests_Fails(self):
        run, baseline = replay("41535048861")
        for i in range(12):
            run["integration (2)"][f"Connapse.Integration.Tests.SlowTests.Added_{i}"] = 5.0 * pt.factor(
                run["integration (2)"], pt.reference(baseline))[0]
        rc, out = main(run, baseline)
        self.assertEqual(rc, 1, out)
        self.assertIn("::error::PrPipeline_WallTimeUnder6Min: the changed tests add 6", out)

    def test_main_OneExistingTestSlowerByAMinute_Fails(self):
        run, baseline = replay("41521110441")
        name = next(n for n in run["medium"] if pt.reference(baseline).get(n, 0) > 0.5)
        run["medium"][name] += 60 * pt.factor(run["medium"], pt.reference(baseline))[0]
        rc, out = main(run, baseline)
        self.assertEqual(rc, 1, out)
        self.assertIn(name, out)

    def test_main_EverySmallTestTwiceAsSlow_FailsPastTheFactorCap(self):
        # A runner factor absorbs a slowdown every test shares, up to MAX_FACTOR; past it the code's.
        run, baseline = replay("41538112206")
        run["small"] = {n: s * 2 * pt.MAX_FACTOR for n, s in run["small"].items()}
        more, _, _, factors = pt.added(run, baseline)
        self.assertEqual(factors["small"], pt.MAX_FACTOR)
        self.assertGreater(more, LIMIT)

    def test_main_ThirtySecondsOfNewTests_Passes(self):
        run, baseline = replay("41567881057")
        run["medium"]["Connapse.Core.Tests.Added"] = 30.0
        rc, out = main(run, baseline)
        self.assertEqual(rc, 0, out)


class Measure(unittest.TestCase):
    def test_changed_InsideTheBand_IsNot(self):
        self.assertFalse(pt.changed(1.9, 0.1))  # under 2 x 0.1 + 2
        self.assertFalse(pt.changed(20, 10))
        self.assertFalse(pt.changed(4.5, 10))

    def test_changed_OutsideTheBand_Is(self):
        self.assertTrue(pt.changed(2.3, 0.1))
        self.assertTrue(pt.changed(22.5, 10))
        self.assertTrue(pt.changed(3.5, 10))

    def test_added_UnchangedTestsOnAFasterRunner_AddNothing(self):
        base = {"medium": {f"T{i}": 1.0 + i / 100 for i in range(60)}}
        run = {"medium": {n: s * 0.7 for n, s in base["medium"].items()}}
        more, _, share, factors = pt.added(run, [base] * 5)
        self.assertAlmostEqual(factors["medium"], 0.7)
        self.assertEqual((more, share), (0, {}))

    def test_added_GoneTest_SavesItsUsualTime(self):
        base = {"medium": {f"T{i}": 1.0 for i in range(60)}}
        run = {"medium": {n: s for n, s in base["medium"].items() if n != "T0"}}
        more, _, share, _ = pt.added(run, [base] * 5)
        self.assertEqual((more, share), (-1.0, {"T0": -1.0}))

    def test_reference_TestOnlyInTheNewestRun_IsKnown(self):
        old = {"small": {"A": 1.0}}
        new = {"small": {"A": 1.0, "B": 2.0}}
        self.assertEqual(pt.reference([new, old, old, old, old]), {"A": 1.0, "B": 2.0})
        self.assertEqual(pt.reference([old, old, new, old, old]), {"A": 1.0})

    def test_factor_FewMatchedTests_IsOne(self):
        self.assertEqual(pt.factor({"A": 3.0}, {"A": 1.0}), (1.0, 1))

    def test_results_GateFiles_AreLeftOut(self):
        trx = ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
               '<UnitTestResult testName="{}" outcome="Passed" duration="00:00:01.5000000" /></Results></TestRun>')
        with tempfile.TemporaryDirectory() as d:
            for name, test in [("small-tests.trx", "S"), ("gate-tests.trx", "G"), ("medium-tests-shard-2.trx", "I"),
                               ("memory-gate-tests.trx", "M")]:
                pathlib.Path(d, name).write_text(trx.format(test), encoding="utf-8")
            self.assertEqual(pt.results(d), {"small": {"S": 1.5}, "integration (2)": {"I": 1.5}})


class Gate(unittest.TestCase):
    def test_main_MissingJobResults_Fails(self):
        run, baseline = replay("41535048861")
        del run["integration (3)"]
        rc, out = main(run, baseline)
        self.assertEqual(rc, 1)
        self.assertIn("::error::no test results from integration (3)", out)

    def test_main_TooFewBaselineRuns_Fails(self):
        run, baseline = replay("41535048861")
        rc, out = main(run, baseline[:4])
        self.assertEqual(rc, 1)
        self.assertIn("only 4 of main's recent runs", out)

    def test_wall_LongestJobPlusThisOne(self):
        js = [job("small", 100), job("integration (1)", 330), {"name": "build-and-test", "started_at": iso(330)}]
        self.assertEqual(pt.wall(js, "build-and-test", T0 + timedelta(seconds=345)), 345)


if __name__ == "__main__":
    unittest.main()
