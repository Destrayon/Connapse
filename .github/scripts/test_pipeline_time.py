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
DRIFT_LIMIT = 120  # ci.yml's --max-drift-seconds
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


def main(run: dict, baseline: list[dict], longest_job: float = 300, own: float = 20,
         base_ref: str = "") -> tuple[int, str]:
    """pipeline_time.main on these results, with GitHub mocked: the longest job ran this long and this
    job has run this long. base_ref is the PR's base branch; empty, a push to main."""
    js = [job("integration (2)", longest_job), job("small", 100),
          {"name": "build-and-test", "started_at": iso(longest_job), "completed_at": None}]
    out = io.StringIO()
    with mock.patch.object(pt, "results", return_value=run), \
         mock.patch.object(pt, "baseline_runs", return_value=[(str(i), b, 340.0 + i) for i, b in enumerate(baseline)]), \
         mock.patch.object(pt, "base_commit", return_value="b" * 40), \
         mock.patch.object(pt, "jobs", return_value=js), \
         mock.patch.object(pt, "utcnow", return_value=T0 + timedelta(seconds=longest_job + own)), \
         mock.patch.dict(os.environ, {"GITHUB_RUN_ID": "1", "GITHUB_STEP_SUMMARY": "",
                                      "GITHUB_BASE_REF": base_ref, "GITHUB_REF_NAME": "main"}), \
         contextlib.redirect_stdout(out):
        rc = pt.main(["--results", ".", "--max-added-seconds", str(LIMIT), "--max-drift-seconds", str(DRIFT_LIMIT),
                      "--max-wall-seconds", "360", "--current", "build-and-test"])
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

    def test_main_ManyTestsEachALittleSlower_FailsOnTheirSum(self):
        # 300 tests each 0.5 s slower on a median runner, every one inside the band: 150 s together.
        run, baseline = replay("41535048861")
        ref = pt.reference(baseline)
        picked = [(job, n) for job, tests in run.items() for n in tests if 0.2 < ref.get(n, 0) < 5][:300]
        self.assertEqual(len(picked), 300)
        for job, n in picked:
            run[job][n] += 0.5 * pt.factor(run[job], ref)[0]
        more, _, _, _ = pt.added(run, baseline)
        self.assertLess(more, LIMIT)
        rc, out = main(run, baseline)
        self.assertEqual(rc, 1, out)
        self.assertIn("a slowdown spread over many tests", out)


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

    def test_added_WaitsOnAFastCpu_AreNotSlower(self):
        # CPU-bound tests at 0.45x, ten-second waits unchanged: the factor would put each wait at 22 s.
        base = {"medium": {**{f"C{i}": 1.0 for i in range(60)}, **{f"W{i}": 10.0 for i in range(4)}}}
        run = {"medium": {**{f"C{i}": 0.45 for i in range(60)}, **{f"W{i}": 10.0 for i in range(4)}}}
        more, _, share, _ = pt.added(run, [base] * 5)
        self.assertEqual((more, share), (0, {}))

    def test_added_JobMissingFromMainsNewestRun_IsNotCompared(self):
        base = {"small": {f"T{i}": 1.0 for i in range(60)}}
        run = {"small": dict(base["small"]), "small gates": {"G": 12.0}}
        self.assertEqual(pt.added(run, [base] * 5)[0], 0)

    def test_added_FileMissingFromThisRun_DropsNoTests(self):
        base = {"small": {f"T{i}": 1.0 for i in range(60)}, "small gates": {"G": 12.0}}
        run = {"small": dict(base["small"])}
        self.assertEqual(pt.added(run, [base] * 5)[0], 0)

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

    def test_results_GateFiles_AreTheirOwnJobs(self):
        trx = ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
               '<UnitTestResult testName="{}" outcome="Passed" duration="00:00:01.5000000" /></Results></TestRun>')
        with tempfile.TemporaryDirectory() as d:
            for name, test in [("small-tests.trx", "S"), ("gate-tests.trx", "G"), ("medium-tests-shard-2.trx", "I"),
                               ("memory-gate-tests.trx", "M")]:
                pathlib.Path(d, name).write_text(trx.format(test), encoding="utf-8")
            self.assertEqual(pt.results(d), {"small": {"S": 1.5}, "small gates": {"G": 1.5},
                                             "integration (2)": {"I": 1.5}, "integration (1) gates": {"M": 1.5}})


class Baseline(unittest.TestCase):
    """main's runs from the base commit's back: a test merged into main after it isn't one this run dropped."""

    RUNS = [{"id": 5, "head_sha": "e" * 40, "status": "completed"},   # merged after the PR's base
            {"id": 4, "head_sha": "d" * 40, "status": "completed"},   # the base
            {"id": 3, "head_sha": "c" * 40, "status": "completed"},
            {"id": 2, "head_sha": "a" * 40, "status": "completed"},
            {"id": 1, "head_sha": "9" * 40, "status": "completed"},
            {"id": 0, "head_sha": "8" * 40, "status": "completed"},
            {"id": -1, "head_sha": "7" * 40, "status": "completed"}]

    # main's runs, one an hour back from 2026-11-10 12:00, and an epic's, from 09:05 on.
    RUNS = [dict(r, created_at=f"2026-11-10T{12 - n:02d}:00:00Z") for n, r in enumerate(RUNS)]
    EPIC =[{"id": 103, "head_sha": "3" * 40, "status": "completed", "created_at": "2026-11-10T09:30:00Z"},  # the base
            {"id": 102, "head_sha": "2" * 40, "status": "completed", "created_at": "2026-11-10T09:20:00Z"},
            {"id": 101, "head_sha": "1" * 40, "status": "completed", "created_at": "2026-11-10T09:10:00Z"},
            {"id": 100, "head_sha": "0" * 40, "status": "completed", "created_at": "2026-11-10T09:05:00Z"}]

    def ids(self, base: str | None, branch: str = "main", epic: list[dict] | None = None) -> tuple[list[str], str]:
        green = [{"name": t, "conclusion": "success"} for t in pt.TEST_JOBS]
        lists = {"main": self.RUNS, "epic/con-e6-tables": self.EPIC if epic is None else epic}
        out = io.StringIO()
        with mock.patch.object(pt, "branch_runs", side_effect=lambda b="main": lists.get(b, [])), \
             mock.patch.object(pt, "jobs", return_value=green), \
             mock.patch.object(pt, "download"), \
             mock.patch.object(pt, "results", return_value={t: {"T": 1.0} for t in pt.TEST_JOBS}), \
             contextlib.redirect_stdout(out):
            return [i for i, _, _ in pt.baseline_runs(exclude="", base=base, branch=branch)], out.getvalue()

    def test_baseline_runs_StartsAtTheBaseCommitsRun(self):
        self.assertEqual(self.ids("d" * 40)[0], ["4", "3", "2", "1", "0"])

    def test_baseline_runs_BaseNotAmongMainsRuns_StartsAtTheNewestAndSaysSo(self):
        ids, out = self.ids("f" * 40)
        self.assertEqual(ids, ["5", "4", "3", "2", "1"])
        self.assertIn("::notice::", out)

    def test_baseline_runs_PrIntoMain_NeverReadsAnotherBranch(self):
        with mock.patch.object(pt, "branch_runs", return_value=self.RUNS) as runs, \
             mock.patch.object(pt, "jobs", return_value=[{"name": t, "conclusion": "success"} for t in pt.TEST_JOBS]), \
             mock.patch.object(pt, "download"), \
             mock.patch.object(pt, "results", return_value={t: {"T": 1.0} for t in pt.TEST_JOBS}), \
             contextlib.redirect_stdout(io.StringIO()):
            pt.baseline_runs(exclude="", base="d" * 40)
        self.assertEqual([c.args for c in runs.call_args_list], [("main",)])

    def test_baseline_runs_EpicWithFiveRuns_IsTheEpicsOwnFromItsBase(self):
        epic = [{"id": 104, "head_sha": "4" * 40, "status": "completed", "created_at": "2026-11-10T09:40:00Z"}] + \
               self.EPIC + [{"id": 99, "head_sha": "9" * 40, "status": "completed", "created_at": "2026-11-10T09:01:00Z"}]
        ids, out = self.ids("3" * 40, "epic/con-e6-tables", epic)
        self.assertEqual(ids, ["103", "102", "101", "100", "99"])
        self.assertNotIn("main's runs fill", out)

    def test_baseline_runs_EpicWithFewerRuns_FillsWithMainsFromBeforeItsOldest(self):
        # The epic's oldest run is 09:05: main's runs from 2 (09:00) back, not the newer ones it may lack.
        ids, out = self.ids("3" * 40, "epic/con-e6-tables")
        self.assertEqual(ids, ["103", "102", "101", "100", "2"])
        self.assertIn("epic/con-e6-tables has 4 usable runs of its own; main's runs fill the other 1", out)

    def test_baseline_runs_EpicWithNoRuns_IsMainsAsForAPrIntoMain(self):
        ids, out = self.ids("3" * 40, "epic/con-e6-tables", epic=[])
        self.assertEqual(ids, ["5", "4", "3", "2", "1"])
        self.assertIn("epic/con-e6-tables has 0 usable runs of its own", out)

    def test_baseline_runs_EpicWithNoRunsTakenFromMainsBase_StartsThere(self):
        # An epic made from main's run 4 and not pushed since: main's runs from 4 back.
        self.assertEqual(self.ids("d" * 40, "epic/con-e6-tables", epic=[])[0], ["4", "3", "2", "1", "0"])

    def test_base_branch_PrIntoAnEpic_IsTheEpic(self):
        with mock.patch.dict(os.environ, {"GITHUB_BASE_REF": "epic/con-e6-tables", "GITHUB_REF_NAME": "1208/merge"}):
            self.assertEqual(pt.base_branch(), "epic/con-e6-tables")

    def test_base_branch_PushToAnEpic_IsTheEpic(self):
        with mock.patch.dict(os.environ, {"GITHUB_BASE_REF": "", "GITHUB_REF_NAME": "epic/con-e6-tables"}):
            self.assertEqual(pt.base_branch(), "epic/con-e6-tables")

    def test_branch_runs_EpicName_IsEscapedInTheQuery(self):
        with mock.patch.object(pt, "gh_json", return_value={"workflow_runs": []}) as gh, \
             mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": "o/r", "GITHUB_WORKFLOW_REF": "o/r/.github/workflows/ci.yml@x"}):
            pt.branch_runs("epic/con-e6-tables")
        self.assertIn("runs?branch=epic%2Fcon-e6-tables&event=push", gh.call_args.args[1])

    def test_base_commit_ShallowCheckout_ReadsTheFirstParent(self):
        raw = f"tree {'1' * 40}\nparent {'d' * 40}\nparent {'f' * 40}\nauthor x\n\nmerge\n"
        with mock.patch.object(pt.subprocess, "run", return_value=mock.Mock(stdout=raw)):
            self.assertEqual(pt.base_commit(), "d" * 40)

    @staticmethod
    def failed(stderr: str) -> pt.subprocess.CalledProcessError:
        return pt.subprocess.CalledProcessError(1, ["gh"], output="", stderr=stderr)

    def test_jobs_OneOffGitHubError_IsRetriedAndSucceeds(self):
        ok = mock.Mock(stdout=json.dumps({"jobs": [{"name": "small"}]}))
        with mock.patch.object(pt.subprocess, "run",
                               side_effect=[self.failed("HTTP 502: Bad Gateway (https://api.github.com/...)"), ok]) as run, \
             mock.patch.object(pt.time, "sleep") as sleep, \
             mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": "o/r"}), \
             contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(pt.jobs(1), [{"name": "small"}])
        self.assertEqual(run.call_count, 2)
        sleep.assert_called_once_with(pt.RETRY_DELAYS[0])
        self.assertIn("retrying", out.getvalue())

    def test_main_OneOffGitHubErrorFetchingTheBaseline_Passes(self):
        # The whole check, a rate limit hitting the first fetch of main's runs: the retry carries it through.
        run, baseline = replay(REPLAYS[0]["run"])
        green = {"jobs": [job(t, 100) for t in pt.TEST_JOBS] + [job("build-and-test", 50)]}
        ok = [mock.Mock(stdout=json.dumps({"workflow_runs": [
            {"id": i, "head_sha": str(i) * 40, "status": "completed", "created_at": iso(i)} for i in range(4, -1, -1)]}))]
        ok += [mock.Mock(stdout=json.dumps(green)), mock.Mock(stdout="")] * 5
        ok += [mock.Mock(stdout=json.dumps({"jobs": [job("integration (2)", 300),
               {"name": "build-and-test", "started_at": iso(300), "completed_at": None}]}))]
        results = iter([run] + baseline)
        with mock.patch.object(pt.subprocess, "run",
                               side_effect=[mock.Mock(stdout=f"tree {'1' * 40}\nparent {'4' * 40}\n"),
                                            self.failed("API rate limit exceeded for installation"), *ok]), \
             mock.patch.object(pt.time, "sleep"), \
             mock.patch.object(pt, "results", side_effect=lambda _: next(results)), \
             mock.patch.object(pt, "utcnow", return_value=T0 + timedelta(seconds=320)), \
             mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": "o/r", "GITHUB_RUN_ID": "99", "GITHUB_BASE_REF": "",
                                          "GITHUB_REF_NAME": "main", "GITHUB_STEP_SUMMARY": ""}), \
             contextlib.redirect_stdout(io.StringIO()) as out:
            rc = pt.main(["--results", ".", "--max-added-seconds", str(LIMIT), "--max-drift-seconds",
                          str(DRIFT_LIMIT), "--max-wall-seconds", "360", "--current", "build-and-test"])
        self.assertEqual(rc, 0, out.getvalue())

    def test_gh_GitHubDownPastEveryRetry_Fails(self):
        with mock.patch.object(pt.subprocess, "run", side_effect=self.failed("HTTP 503: Service Unavailable")) as run, \
             mock.patch.object(pt.time, "sleep"), contextlib.redirect_stdout(io.StringIO()), \
             self.assertRaises(pt.subprocess.CalledProcessError):
            pt.gh("api", "x")
        self.assertEqual(run.call_count, len(pt.RETRY_DELAYS) + 1)

    def test_gh_NotFound_FailsWithoutARetry(self):
        with mock.patch.object(pt.subprocess, "run", side_effect=self.failed("HTTP 404: Not Found")) as run, \
             mock.patch.object(pt.time, "sleep") as sleep, self.assertRaises(pt.subprocess.CalledProcessError):
            pt.gh("api", "x")
        self.assertEqual(run.call_count, 1)
        sleep.assert_not_called()


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

    @staticmethod
    def tables_epic(run: dict, baseline: list[dict]) -> tuple[dict, list[dict]]:
        """The tables epic's shape: its earlier PRs' tests add 50 s on a median runner, in every epic run, and
        this PR's own test adds 1.9 s. Returns this run and the epic's own runs as its baseline."""
        ref = pt.reference(baseline)
        epic = {f"Connapse.Integration.Tests.Tables.EarlierEpicPr_{i}": 10.0 for i in range(5)}
        f = pt.factor(run["integration (1)"], ref)[0]
        run["integration (1)"].update({n: s * f for n, s in epic.items()})
        run["integration (1)"]["Connapse.Integration.Tests.Tables.ThisPr"] = 1.9 * f
        epic_runs = json.loads(json.dumps(baseline))
        for b in epic_runs:
            b["integration (1)"].update({n: s * pt.factor(b["integration (1)"], ref)[0] for n, s in epic.items()})
        return run, epic_runs

    def test_main_PrIntoAnEpicAgainstMain_FailsForTheEpicsEarlierTests(self):
        run, baseline = replay("41535048861")
        run, _ = self.tables_epic(run, baseline)
        rc, out = main(run, baseline)
        self.assertEqual(rc, 1, out)

    def test_main_PrIntoAnEpicAgainstTheEpicsRuns_CountsOnlyItsOwnTests(self):
        run, baseline = replay("41535048861")
        run, epic_runs = self.tables_epic(run, baseline)
        more, _, share, _ = pt.added(run, epic_runs)
        self.assertLess(more, 10)
        self.assertNotIn("Connapse.Integration.Tests.Tables.EarlierEpicPr_0", share)
        rc, out = main(run, epic_runs, base_ref="epic/con-e6-tables")
        self.assertEqual(rc, 0, out)
        self.assertIn("epic/con-e6-tables's usual", out)

    def test_wall_LongestJobPlusThisOne(self):
        js = [job("small", 100), job("integration (1)", 330), {"name": "build-and-test", "started_at": iso(330)}]
        self.assertEqual(pt.wall(js, "build-and-test", T0 + timedelta(seconds=345)), 345)


if __name__ == "__main__":
    unittest.main()
