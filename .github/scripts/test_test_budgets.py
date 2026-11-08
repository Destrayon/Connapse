"""Tests for test_budgets.py (CON-87). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"."""
import contextlib
import io
import json
import os
import pathlib
import sys
import tempfile
import unittest
from xml.sax.saxutils import quoteattr

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import test_budgets as b  # noqa: E402

# A runner's speed shows in every test's duration: these are a median runner's.
REFERENCE = {f"T.Test{i}": 0.05 + 0.01 * (i % 7) for i in range(200)}


def trx(durations: dict[str, float]) -> str:
    rows = "".join(
        f'<UnitTestResult testName={quoteattr(name)} outcome="Passed" duration="00:00:{seconds:09.6f}" />'
        for name, seconds in durations.items())
    return ('<?xml version="1.0" encoding="utf-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{rows}</Results></TestRun>')


@contextlib.contextmanager
def tier(durations: dict[str, float], reference: dict[str, float] | None = REFERENCE):
    """A checkout with one test project whose Small run took these durations, and a reference file."""
    with tempfile.TemporaryDirectory() as root:
        cwd = os.getcwd()
        os.chdir(root)
        try:
            pathlib.Path("tests/P").mkdir(parents=True)
            pathlib.Path("tests/P/P.csproj").write_text('<PackageReference Include="Microsoft.NET.Test.Sdk" />')
            pathlib.Path("tests/P/TestResults").mkdir()
            pathlib.Path("tests/P/TestResults/small-tests.trx").write_text(trx(durations), encoding="utf-8")
            pathlib.Path("Connapse.slnx").write_text('<Project Path="tests/P/P.csproj" />')
            if reference is not None:
                pathlib.Path("reference.json").write_text(json.dumps(reference))
            yield
        finally:
            os.chdir(cwd)


def run(*args: str) -> tuple[int, str]:
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = b.main(["--tier", "Small", "--trx", "small-tests.trx", "--max-test-seconds", "10", *args])
    return code, out.getvalue()


def scaled(factor: float) -> dict[str, float]:
    return {name: seconds * factor for name, seconds in REFERENCE.items()}


class RunnerFactor(unittest.TestCase):
    def test_is_the_median_ratio_of_each_test_to_its_reference(self):
        durations = scaled(2.0) | {"T.Test0": 50.0}  # one outlier moves a mean, not the median
        factor, matched = b.runner_factor(durations, REFERENCE)
        self.assertAlmostEqual(factor, 2.0)
        self.assertEqual(matched, 200)

    def test_counts_only_tests_the_reference_names(self):
        factor, matched = b.runner_factor({"T.Test1": 0.12, "New.Test": 9.0}, REFERENCE)
        self.assertAlmostEqual(factor, 2.0)
        self.assertEqual(matched, 1)


class SmallWall(unittest.TestCase):
    def test_a_slow_runner_passes_where_the_raw_wall_is_over(self):
        # The run that failed at 31 s, as #925, #928 and #921 did: every test 1.25x as slow as the reference.
        with tier(scaled(1.25)):
            code, out = run("--wall-seconds", "31", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 0, out)
        self.assertIn("24.8 s", out)

    def test_a_slowdown_of_every_test_cant_pass_as_a_slow_runner(self):
        # Every test twice as slow on a median runner, 50 s wall: the factor is capped at the slowest
        # runner seen, so this reads as 33.3 s, not the 25 s a factor of 2 would make it.
        with tier(scaled(2.0)):
            code, out = run("--wall-seconds", "50", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)
        self.assertIn("33.3 s", out)

    def test_a_reference_most_of_whose_tests_no_longer_run_fails_and_says_to_refresh(self):
        # 150 of the 200 reference tests ran: enough for a median, but the reference has drifted.
        with tier({name: seconds for name, seconds in list(REFERENCE.items())[:150]}):
            code, out = run("--wall-seconds", "10", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)
        self.assertIn("refresh", out)

    def test_a_fast_runner_is_read_on_its_raw_wall(self):
        # #931's run 40215004273: 14 s on a 0.45x runner read as 31.2 s and failed (CON-90). About 4 s of
        # the wall doesn't shrink on a fast runner, so dividing all of it by the factor inflates it.
        with tier(scaled(0.45)):
            code, out = run("--wall-seconds", "14", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 0, out)
        self.assertIn("14.0 s", out)

    def test_a_fast_runner_under_the_limit_never_reads_as_over_it(self):
        for factor in (0.3, 0.45, 0.6, 0.9, 0.99):
            for wall in (14, 25, 30):
                with self.subTest(factor=factor, wall=wall), tier(scaled(factor)):
                    code, out = run("--wall-seconds", str(wall), "--max-wall-seconds", "30", "--reference", "reference.json")
                    self.assertEqual(code, 0, out)

    def test_a_fast_runner_over_the_limit_still_fails(self):
        with tier(scaled(0.45)):
            code, out = run("--wall-seconds", "31", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)
        self.assertIn("31.0 s", out)

    def test_a_slower_start_on_a_median_runner_fails(self):
        with tier(scaled(1.0)):
            code, out = run("--wall-seconds", "35", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)

    def test_too_few_reference_tests_ran_fails_and_says_to_refresh(self):
        with tier({"T.Test1": 0.06, "Other.Test": 0.1}):
            code, out = run("--wall-seconds", "10", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)
        self.assertIn("refresh", out)

    def test_without_a_reference_the_raw_wall_is_checked_as_before(self):
        with tier(scaled(2.0), reference=None):
            code, out = run("--wall-seconds", "31", "--max-wall-seconds", "30")
        self.assertEqual(code, 1, out)


class SlowRunnerRun41957977547(unittest.TestCase):
    """#1133's run 41957977547 failed at 30.8 s with no slower test, and its rerun passed (CON-250).

    The fixture is that run's durations of the 809 tests the reference times (115 s of its 6,898). GitHub
    replaced the failing attempt's artifact with the rerun's, so these are the rerun's (same commit) scaled
    by 1.116 to the failing attempt's factor of 1.01 against the old reference; the whole rerun scaled
    so sums to 154 s, within 2% of the failing attempt's 151.5 s.

    The reference is the one CON-250 committed, frozen here, so refreshing the live one can't move these
    numbers."""

    SCRIPTS = pathlib.Path(__file__).resolve().parent
    RUN = json.loads((SCRIPTS / "testdata" / "run-41957977547-small.json").read_text(encoding="utf-8"))
    CHECKED_IN = json.loads((SCRIPTS / "testdata" / "small-reference-2026-11-04.json").read_text(encoding="utf-8"))

    def check(self, durations: dict[str, float], wall: float) -> tuple[int, str]:
        with tier(durations, self.CHECKED_IN):
            return run("--wall-seconds", f"{wall:g}", "--max-wall-seconds", "30", "--reference", "reference.json")

    def test_its_31_s_on_a_slow_runner_passes(self):
        code, out = self.check(self.RUN, 31)
        self.assertEqual(code, 0, out)
        self.assertIn("22.8 s on a fast runner", out)

    def test_its_31_s_failed_against_a_reference_that_reads_typical_runners_at_080(self):
        # The cause, kept: against a reference 1.35x slower than main's tests, as the one written on
        # 2026-10-25 had become, that runner reads 1.01, as it did, and gets 1% of the relief it needs.
        stale = {name: seconds * 1.35 for name, seconds in self.CHECKED_IN.items()}
        with tier(self.RUN, stale):
            code, out = run("--wall-seconds", "31", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)

    def test_a_new_slow_class_on_that_runner_still_fails(self):
        # A PR adds a class whose three 4 s tests run one after another, after the rest: the wall grows
        # by 12 s, about 9 s on a median runner, and the factor doesn't move.
        new = {f"New.SlowClass.Test{i}": 4.0 for i in range(3)}
        code, out = self.check(self.RUN | new, 31 + 12)
        self.assertEqual(code, 1, out)
        self.assertIn("Small tier took 31.6 s on a fast runner", out)

    def test_every_test_60_percent_slower_on_that_runner_still_fails(self):
        # A slowdown of every test alike reads as a slower runner until the factor reaches MAX_FACTOR.
        code, out = self.check({name: seconds * 1.6 for name, seconds in self.RUN.items()}, 31 * 1.6)
        self.assertEqual(code, 1, out)
        self.assertIn("capped at 1.5", out)


class CheckReference(unittest.TestCase):
    """The nightly's check that the reference still describes main's faster runners (CON-250, CON-294)."""

    @staticmethod
    def runs(root: str, factors: list[float], tests: int | list[int] = len(REFERENCE)) -> list[str]:
        counts = tests if isinstance(tests, list) else [tests] * len(factors)
        dirs = []
        for i, (factor, count) in enumerate(zip(factors, counts)):
            d = pathlib.Path(root, f"run{i}", "tests", "P", "TestResults")
            d.mkdir(parents=True)
            (d / "small-tests.trx").write_text(trx(dict(list(scaled(factor).items())[:count])))
            dirs.append(str(pathlib.Path(root, f"run{i}")))
        return dirs

    def check(self, factors: list[float], tests: int | list[int] = len(REFERENCE)) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as root:
            pathlib.Path(root, "reference.json").write_text(json.dumps(REFERENCE))
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = b.main(["--trx", "small-tests.trx", "--check-reference", str(pathlib.Path(root, "reference.json")),
                               *self.runs(root, factors, tests)])
            return code, out.getvalue()

    # main's two kinds of runner against a fresh reference (CON-294): the faster at about 1, the slower at
    # about 1.4, and 10 of 15 runs the slower kind, as main's latest 15 were on 2026-11-08.
    FLEET = [1.0, 1.4, 1.05, 1.45, 1.35, 0.95, 1.4, 1.4, 1.0, 1.3, 1.4, 1.4, 1.05, 1.5, 1.35]

    def test_a_fresh_reference_passes_on_a_mostly_slow_fleet(self):
        code, out = self.check(self.FLEET)
        self.assertEqual(code, 0, out)
        self.assertIn("the fastest quarter at a median runner factor of 1.00", out)

    def test_a_reference_written_on_the_slower_kind_fails_and_says_to_refresh(self):
        # CON-286's reference: the median of a mostly slow sample, so the faster kind reads about 0.72.
        code, out = self.check([f / 1.4 for f in self.FLEET])
        self.assertEqual(code, 1, out)
        self.assertIn("refresh", out)

    def test_a_reference_main_has_outrun_fails_and_says_to_refresh(self):
        # The reference CON-250 found: main's runs read 0.80 against it, tests faster, runners not.
        code, out = self.check([f * 0.73 for f in self.FLEET])
        self.assertEqual(code, 1, out)
        self.assertIn("refresh", out)

    def test_a_reference_main_has_slowed_past_fails(self):
        code, out = self.check([f * 1.3 for f in self.FLEET])
        self.assertEqual(code, 1, out)

    def test_too_few_of_the_faster_kind_keeps_the_reference_without_asking_for_a_refresh(self):
        # 2 of 15 runs on the faster kind: the fastest quarter reads the slower kind at 1.4, and a refresh
        # from these runs would be the slower kind's.
        code, out = self.check([1.0, 1.05] + [1.4] * 13)
        self.assertEqual(code, 0, out)
        self.assertIn("only 2 of 15 runs are GitHub's faster kind", out)
        self.assertNotIn("refresh", out)

    def test_drift_found_in_fewer_runs_than_a_refresh_takes_says_to_download_more(self):
        code, out = self.check([f * 0.73 for f in self.FLEET])
        self.assertEqual(code, 1, out)
        self.assertIn("these 15 runs are too few to write one from, so download 20 or more", out)

    def test_too_few_runs_fail(self):
        code, out = self.check(self.FLEET[:9])
        self.assertEqual(code, 1, out)
        self.assertIn("needs 10", out)

    def test_runs_that_timed_too_few_of_the_reference_tests_are_left_out(self):
        # 150 of the 200: a drifted test list, not a runner's speed.
        code, out = self.check(self.FLEET, tests=150)
        self.assertEqual(code, 1, out)
        self.assertIn("reads 0 of 15 runs", out)

    def test_newest_runs_that_left_the_reference_behind_fail_even_with_ten_healthy_ones(self):
        # Codex's case: five newest runs carry 150 of the 200 tests, ten older ones all of them.
        code, out = self.check(self.FLEET, tests=[150] * 5 + [len(REFERENCE)] * 10)
        self.assertEqual(code, 1, out)
        self.assertIn("5 of 15 runs timed too few", out)

    def test_one_or_two_crashed_runs_are_left_out_without_failing(self):
        code, out = self.check(self.FLEET, tests=[150] * 2 + [len(REFERENCE)] * 13)
        self.assertEqual(code, 0, out)
        self.assertIn("reads 13 of 15 runs", out)


class WriteReference(unittest.TestCase):
    # Enough other tests that a runner's speed can be read from the reference.
    FILLERS = {f"F.Test{j}": 0.05 for j in range(b.MIN_MATCHED)}

    @staticmethod
    def runs(root: str, factors: list[float], fillers: dict[str, float] = FILLERS) -> list[str]:
        dirs = []
        for i, factor in enumerate(factors):
            d = pathlib.Path(root, f"run{i}", "tests", "P", "TestResults")
            d.mkdir(parents=True)
            durations = {"A": 0.1 * factor, "Tiny": 0.001, "B": 0.2 * factor}
            durations |= {name: seconds * factor for name, seconds in fillers.items()}
            if i != 1:
                durations["Sometimes"] = 0.5
            (d / "small-tests.trx").write_text(trx(durations))
            dirs.append(str(pathlib.Path(root, f"run{i}")))
        return dirs

    def test_keeps_each_tests_median_over_the_fastest_quarter_for_tests_every_run_timed_at_10_ms_or_more(self):
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")

            # The fastest quarter of 20 runs is the five at 2.6 to 3.4, whose median is 3.
            reference = b.write_reference(str(out), "small-tests.trx", self.runs(root, [6.0] * 15 + [3.4, 3.2, 3.0, 2.8, 2.6]))

            self.assertEqual(set(reference), {"A", "B"} | set(self.FILLERS))
            self.assertAlmostEqual(reference["A"], 0.3)
            self.assertAlmostEqual(reference["B"], 0.6)
            self.assertEqual(json.loads(out.read_text()), reference)

    def test_from_a_mostly_slow_fleet_it_is_the_faster_kinds(self):
        # 6 of 20 runs on the faster kind, 14 on one 1.4x slower: the median run is the slower kind's.
        with tempfile.TemporaryDirectory() as root:
            factors = [0.95, 1.0, 1.0, 1.0, 1.05, 1.1] + [1.4] * 14
            reference = b.write_reference(str(pathlib.Path(root, "reference.json")), "small-tests.trx", self.runs(root, factors))

            self.assertAlmostEqual(reference["A"], 0.1)

    def test_from_too_few_of_the_faster_kind_keeps_the_old_reference(self):
        # 2 of 20 on the faster kind: the fastest quarter would be 2 of them and 3 of the slower kind.
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")
            out.write_text("old")

            with self.assertRaisesRegex(ValueError, "only 2 of 20 runs are GitHub's faster kind"):
                b.write_reference(str(out), "small-tests.trx", self.runs(root, [1.0, 1.05] + [1.4] * 18))
            self.assertEqual(out.read_text(), "old")

    def test_from_one_kind_alone_it_is_that_kinds_fastest_quarter(self):
        with tempfile.TemporaryDirectory() as root:
            factors = [0.9 + 0.01 * i for i in range(20)]
            reference = b.write_reference(str(pathlib.Path(root, "reference.json")), "small-tests.trx", self.runs(root, factors))

            self.assertAlmostEqual(reference["A"], 0.092)

    def test_with_too_few_tests_to_read_a_runner_keeps_the_old_reference(self):
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")
            out.write_text("old")

            with self.assertRaisesRegex(ValueError, "only 2 tests"):
                b.write_reference(str(out), "small-tests.trx", self.runs(root, [1.0] * 5 + [1.4] * 15, fillers={}))
            self.assertEqual(out.read_text(), "old")

    def test_pr_1171s_slow_runner_passes_against_a_reference_from_main_mixed_fleet(self):
        # #1171 failed at 30.9 s on the slower kind against CON-286's reference, which that kind read at
        # 1.07, then passed at 20 s on a rerun. Written from a fleet like main's, the reference reads that
        # runner at 1.4 and its 31 s as 22.1 s.
        with tempfile.TemporaryDirectory() as root:
            dirs = CheckReference.runs(root, [0.95, 1.0, 1.0, 1.0, 1.05, 1.1] + [1.4] * 14)
            reference = b.write_reference(str(pathlib.Path(root, "reference.json")), "small-tests.trx", dirs)
        with tier(scaled(1.4), reference):
            code, out = run("--wall-seconds", "31", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 0, out)
        self.assertIn("22.1 s on a fast runner", out)

    def test_from_fewer_than_twenty_runs_writes_nothing(self):
        # Fewer, and the fastest quarter is too few runs to be the faster kind's speed rather than one runner's.
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")

            with self.assertRaises(ValueError):
                b.write_reference(str(out), "small-tests.trx", self.runs(root, [float(f) for f in range(1, 20)]))
            self.assertFalse(out.exists())

    def test_a_run_with_no_results_writes_nothing(self):
        with tempfile.TemporaryDirectory() as root:
            dirs = self.runs(root, [float(f) for f in range(1, 20)]) + [str(pathlib.Path(root, "empty"))]
            out = pathlib.Path(root, "reference.json")

            with self.assertRaises(ValueError):
                b.write_reference(str(out), "small-tests.trx", dirs)
            self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
