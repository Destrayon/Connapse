"""Tests for pipeline_wall.py (CON-127). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"."""
import argparse
import contextlib
import copy
import io
import json
import pathlib
import sys
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import pipeline_wall as pw  # noqa: E402

T0 = datetime(2026, 10, 28, 5, 0, 0, tzinfo=timezone.utc)
ALLOWANCES = ["Upload coverage reports=15", "Wait for container images=15"]


def iso(seconds: float) -> str:
    return (T0 + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")


def job(name: str, steps: list[tuple[str, float]], start: float = 0) -> dict:
    """A finished job whose steps ran back to back for these many seconds each."""
    rows, t = [], start
    for step, seconds in steps:
        rows.append({"name": step, "started_at": iso(t), "completed_at": iso(t + seconds)})
        t += seconds
    return {"name": name, "conclusion": "success", "started_at": iso(start), "completed_at": iso(t), "steps": rows}


# Integration shard 2 of run 40630926443 (#1012): a 432 s job whose coverage upload took 105 s, not 3 s.
SLOW_UPLOAD = job("integration (2)", [("Run ./.github/actions/build", 115), ("Run ./.github/actions/ollama", 18),
                                      ("Run Integration.Tests shard 2 of 3", 187), ("Upload coverage reports", 105),
                                      ("Other steps", 7)])


SETUP_FILE = pathlib.Path(__file__).parent.parent / "setup-allowances.json"


def run(jobs: list[dict], own_seconds: float = 11, max_seconds: float = 360,
        allowances: list[str] = ALLOWANCES, setup: pathlib.Path | None = None) -> tuple[int, str]:
    now = T0 + timedelta(seconds=600)
    current = {"name": "build-and-test", "started_at": iso(600 - own_seconds), "completed_at": None, "steps": []}
    args = ["pipeline_wall.py", "--max-seconds", str(max_seconds), "--current", "build-and-test"]
    for a in allowances:
        args += ["--network-step", a]
    if setup:
        args += ["--setup-allowances", str(setup)]
    out = io.StringIO()
    with mock.patch.object(pw, "jobs", return_value=jobs + [current]), \
         mock.patch.object(pw, "utcnow", return_value=now), \
         mock.patch.object(sys, "argv", args), contextlib.redirect_stdout(out):
        rc = pw.main()
    return rc, out.getvalue()


class Counted(unittest.TestCase):
    def test_counted_SlowCoverageUpload_CountsOnlyItsAllowance(self):
        seconds, over = pw.counted(SLOW_UPLOAD, {"Upload coverage reports": 15})
        self.assertEqual(seconds, 432 - 90)
        self.assertEqual(over, [("Upload coverage reports", 105, 15)])

    def test_counted_NetworkStepWithinAllowance_CountsItInFull(self):
        j = job("integration (1)", [("Run tests", 200), ("Upload coverage reports", 3)])
        self.assertEqual(pw.counted(j, {"Upload coverage reports": 15}), (203, []))

    def test_counted_SlowTestStep_CountsInFull(self):
        j = job("integration (1)", [("Run tests", 500), ("Upload coverage reports", 3)])
        self.assertEqual(pw.counted(j, {"Upload coverage reports": 15}), (503, []))

    def test_counted_StepWithoutTimes_IsIgnored(self):
        j = job("integration (1)", [("Run tests", 200)])
        j["steps"].append({"name": "Upload coverage reports", "started_at": None, "completed_at": None})
        self.assertEqual(pw.counted(j, {"Upload coverage reports": 15}), (200, []))


class Allowance(unittest.TestCase):
    def test_allowance_NameWithEquals_SplitsOnTheLast(self):
        self.assertEqual(pw.allowance("a=b=20"), ("a=b", 20.0))

    def test_allowance_NoSeconds_IsAnError(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            pw.allowance("Upload coverage reports")


class Main(unittest.TestCase):
    def test_main_Run40630926443SlowUpload_Passes(self):
        rc, out = run([SLOW_UPLOAD, job("integration (3)", [("Run tests", 323)])])
        self.assertEqual(rc, 0, out)
        self.assertIn("::warning::", out)
        self.assertIn("Upload coverage reports took 105 s", out)

    def test_main_SlowImagePull_Passes(self):
        # Run 40636001981 (#1007): the registry took 78 s longer to serve MinIO than usual.
        j = job("integration (2)", [("Run ./.github/actions/build", 115), ("Wait for container images", 93),
                                    ("Run Integration.Tests shard 2 of 3", 170), ("Other steps", 20)])
        rc, out = run([j])
        self.assertEqual(rc, 0, out)
        self.assertIn("Wait for container images took 93 s", out)

    def test_main_RealSlowdownInTests_Fails(self):
        j = job("integration (2)", [("Run ./.github/actions/build", 115), ("Run Integration.Tests shard 2 of 3", 260),
                                    ("Upload coverage reports", 3)])
        rc, out = run([j])
        self.assertEqual(rc, 1, out)
        self.assertIn("::error::PrPipeline_WallTimeUnder6Min", out)

    def test_main_NoAllowances_CountsEverything(self):
        rc, out = run([SLOW_UPLOAD], allowances=[])
        self.assertEqual(rc, 1, out)

    def test_main_LongestJobChosenAfterAllowances(self):
        # The slow upload makes shard 2 the longest raw; after its allowance shard 3 is the longest.
        rc, out = run([SLOW_UPLOAD, job("integration (3)", [("Run tests", 345)])])
        self.assertEqual(rc, 0, out)
        self.assertIn("pipeline: integration (3) 345 s + build-and-test 11 s = 356 s", out)


# Integration shard 2 of run 40732481457's first attempt (#1024): its build took 163 s, not the usual 123 s,
# and with build-and-test's 18 s the pipeline read 367 s. The re-run passed with the same code.
SLOW_BUILD = job("integration (2)", [("Checkout code", 4), ("Start pulling container images", 5),
                                     ("Run ./.github/actions/build", 163), ("Run ./.github/actions/ollama", 16),
                                     ("Run Integration.Tests shard 2 of 3", 141), ("Upload coverage reports", 20),
                                     ("Other steps", 5)])


class SetupAllowances(unittest.TestCase):
    def test_job_setup_MatrixJob_UsesItsJobsSteps(self):
        setup = {"integration": {"Run ./.github/actions/build": 123}, "medium": {"Run ./.github/actions/build": 153}}
        self.assertEqual(pw.job_setup("integration (2)", setup), {"Run ./.github/actions/build": 123})

    def test_job_setup_JobWithoutSetupSteps_HasNone(self):
        self.assertEqual(pw.job_setup("build-and-test", {"small": {"x": 1}}), {})

    def test_counted_FastSetup_CountsItsUsualTime(self):
        j = job("integration (1)", [("Run ./.github/actions/build", 90), ("Run tests", 200)])
        self.assertEqual(pw.counted(j, {}, {"Run ./.github/actions/build": 123}),
                         (323, [("Run ./.github/actions/build", 90, 123)]))

    def test_counted_SetupPastItsCeiling_CountsTheTimeOverIt(self):
        # 123 s usual: a slow runner explains up to 184.5 s, so a 223 s build counts 123 + 38.5.
        j = job("integration (1)", [("Run ./.github/actions/build", 223), ("Run tests", 200)])
        self.assertEqual(pw.counted(j, {}, {"Run ./.github/actions/build": 123})[0], 200 + 123 + 38.5)

    def test_ceiling_ShortStep_GetsAtLeast30Seconds(self):
        self.assertEqual(pw.ceiling(2), 32)
        self.assertEqual(pw.ceiling(123), 184.5)

    def test_main_Run40732481457SlowBuild_Passes(self):
        rc, out = run([SLOW_BUILD], own_seconds=18, setup=SETUP_FILE)
        self.assertEqual(rc, 0, out)
        self.assertIn("Run ./.github/actions/build took 163 s", out)

    def test_main_Run40732481457SlowBuildWithoutSetupAllowances_Fails(self):
        rc, out = run([SLOW_BUILD], own_seconds=18)
        self.assertEqual(rc, 1, out)

    def test_main_RealSlowdownInTestsOnUsualSetup_Fails(self):
        # The same shard with a usual build and 60 s more in its tests: that is the PR, and it still fails.
        j = job("integration (2)", [("Checkout code", 2), ("Run ./.github/actions/build", 123),
                                    ("Run ./.github/actions/ollama", 19), ("Run Integration.Tests shard 2 of 3", 210),
                                    ("Upload coverage reports", 4)])
        rc, out = run([j], setup=SETUP_FILE)
        self.assertEqual(rc, 1, out)
        self.assertIn("::error::PrPipeline_WallTimeUnder6Min", out)

    def test_main_RealSlowdownInTestsOnFastSetup_Fails(self):
        # A runner fast at setup doesn't make room for the slower tests: setup counts its usual time.
        j = job("integration (2)", [("Checkout code", 1), ("Run ./.github/actions/build", 90),
                                    ("Run ./.github/actions/ollama", 12), ("Run Integration.Tests shard 2 of 3", 210),
                                    ("Upload coverage reports", 4)])
        rc, out = run([j], setup=SETUP_FILE)
        self.assertEqual(rc, 1, out)

    def test_main_RealBuildSlowdown_Fails(self):
        # A PR that makes the build 300 s slower: far past what a slow runner explains.
        j = job("integration (2)", [("Checkout code", 2), ("Run ./.github/actions/build", 423),
                                    ("Run ./.github/actions/ollama", 19), ("Run Integration.Tests shard 2 of 3", 141),
                                    ("Upload coverage reports", 4)])
        rc, out = run([j], setup=SETUP_FILE)
        self.assertEqual(rc, 1, out)
        self.assertIn("over the 184.5 s a slow runner explains", out)

    def test_setupAllowancesFile_EveryJobAndStep_IsInTheWorkflow(self):
        # A job or step renamed in ci.yml would otherwise silently count its setup in full.
        root = SETUP_FILE.parent
        workflow = (root / "workflows" / "ci.yml").read_text(encoding="utf-8")
        actions = "".join(p.read_text(encoding="utf-8") for p in (root / "actions").glob("*/action.yml"))
        for name, steps in json.loads(SETUP_FILE.read_text(encoding="utf-8")).items():
            self.assertIn(f"\n  {name}:\n", workflow, name)
            for step in steps:
                # A step without a name is shown as "Run <uses>", or "Build <uses>" for a Docker action.
                uses = step.split(" ", 1)[1] if step.startswith(("Run ", "Build ")) else None
                self.assertTrue(f"name: {step}\n" in workflow + actions or (uses and f"uses: {uses}" in workflow),
                                f"{name}: {step}")


# Run 41535048861's first attempt (#1103), as the jobs API returned it. Integration shard 2's last step
# finished at 22:16:53, but GitHub marked the job complete at 22:18:17: 84 s outside any step, against a
# usual 3 s (85 s with the second before its first step). Its tests took 164 s (171 s on the passing re-run). The check ran at 22:18:28 and read 414 s.
RUN_1103 = json.loads((pathlib.Path(__file__).parent / "fixtures" / "run-41535048861-attempt-1.json")
                      .read_text(encoding="utf-8"))["jobs"]
RUN_1103_NOW = datetime(2026, 11, 2, 22, 18, 28, tzinfo=timezone.utc)


def run_1103(jobs: list[dict], outside: str | None = "3") -> tuple[int, str]:
    args = ["pipeline_wall.py", "--max-seconds", "360", "--current", "build-and-test",
            "--setup-allowances", str(SETUP_FILE)]
    for a in ALLOWANCES:
        args += ["--network-step", a]
    if outside is not None:
        args += ["--outside-steps", outside]
    out = io.StringIO()
    with mock.patch.object(pw, "jobs", return_value=jobs), \
         mock.patch.object(pw, "utcnow", return_value=RUN_1103_NOW), \
         mock.patch.object(sys, "argv", args), contextlib.redirect_stdout(out):
        rc = pw.main()
    return rc, out.getvalue()


def later(s: str, seconds: float) -> str:
    return iso((pw.at(s) - T0).total_seconds() + seconds)


def slower_tests(jobs: list[dict], name: str, step: str, seconds: float) -> list[dict]:
    """These jobs with one job's test step taking this many seconds longer, and every step after it shifted."""
    jobs = copy.deepcopy(jobs)
    j = next(j for j in jobs if j["name"] == name)
    shifting = False
    for s in j["steps"]:
        if shifting:
            s["started_at"] = later(s["started_at"], seconds)
        if s["name"] == step:
            shifting = True
        if shifting:
            s["completed_at"] = later(s["completed_at"], seconds)
    j["completed_at"] = later(j["completed_at"], seconds)
    return jobs


class OutsideSteps(unittest.TestCase):
    def test_counted_TimeOutsideSteps_CountsItsUsualTime(self):
        j = job("integration (2)", [("Run tests", 200)])
        j["completed_at"] = iso(284)
        self.assertEqual(pw.counted(j, {}, outside=3), (203, [(pw.OUTSIDE, 84, 3)]))

    def test_counted_NoTimeOutsideSteps_StillCountsItsUsualTime(self):
        # Both ways, as with setup: a job GitHub closed at once doesn't make room for slower tests.
        self.assertEqual(pw.counted(job("integration (2)", [("Run tests", 200)]), {}, outside=3),
                         (203, [(pw.OUTSIDE, 0, 3)]))

    def test_counted_WithoutOutsideSteps_CountsTheJobAsItRan(self):
        j = job("integration (2)", [("Run tests", 200)])
        j["completed_at"] = iso(284)
        self.assertEqual(pw.counted(j, {}), (284, []))

    def test_counted_JobWithoutStepTimes_CountsTheJobAsItRan(self):
        j = job("integration (2)", [("Run tests", 200)])
        j["steps"][0]["completed_at"] = None
        self.assertEqual(pw.counted(j, {}, outside=3), (200, []))

    def test_main_Run1103WithoutOutsideSteps_FailsAsItDid(self):
        rc, out = run_1103(RUN_1103, outside=None)
        self.assertEqual(rc, 1, out)
        self.assertIn("pipeline: integration (2) 405 s + build-and-test 9 s = 414 s", out)

    def test_main_Run1103SlowJobClose_Passes(self):
        rc, out = run_1103(RUN_1103)
        self.assertEqual(rc, 0, out)
        self.assertIn("::warning::integration (2): 85 s outside its steps", out)
        self.assertIn("pipeline: integration (2) 323 s + build-and-test 9 s = 332 s", out)

    def test_main_Run1103With60sMoreTests_Fails(self):
        # The same run, with a PR that adds a minute to shard 2's tests: that is the PR's time, counted in full.
        rc, out = run_1103(slower_tests(RUN_1103, "integration (2)", "Run Integration.Tests shard 2 of 3", 60))
        self.assertEqual(rc, 1, out)
        self.assertIn("pipeline: integration (2) 383 s + build-and-test 9 s = 392 s", out)
        self.assertIn("::error::PrPipeline_WallTimeUnder6Min", out)

    def test_main_Run1103With60sMoreTestsOnAnotherShard_Fails(self):
        rc, out = run_1103(slower_tests(RUN_1103, "integration (3)", "Run Integration.Tests shard 3 of 3", 60))
        self.assertEqual(rc, 1, out)
        self.assertIn("::error::PrPipeline_WallTimeUnder6Min", out)

    def test_outsideSteps_Negative_IsAnError(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            pw.nonnegative("-1")


if __name__ == "__main__":
    unittest.main()
