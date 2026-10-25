"""Tests for test_budgets.py (CON-87). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"."""
import contextlib
import io
import json
import os
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import test_budgets as b  # noqa: E402

# A runner's speed shows in every test's duration: these are a median runner's.
REFERENCE = {f"T.Test{i}": 0.05 + 0.01 * (i % 7) for i in range(200)}


def trx(durations: dict[str, float]) -> str:
    rows = "".join(
        f'<UnitTestResult testName="{name}" outcome="Passed" duration="00:00:{seconds:09.6f}" />'
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
            pathlib.Path("tests/P/TestResults/small-tests.trx").write_text(trx(durations))
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

    def test_a_real_slowdown_on_a_fast_runner_fails_though_its_raw_wall_is_under(self):
        # A fast runner (0.6x) whose run took 25 s: 41.7 s on a median runner. The raw check passed this.
        with tier(scaled(0.6)):
            code, out = run("--wall-seconds", "25", "--max-wall-seconds", "30", "--reference", "reference.json")
        self.assertEqual(code, 1, out)
        self.assertIn("41.7 s", out)

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


class WriteReference(unittest.TestCase):
    @staticmethod
    def runs(root: str, factors: list[float]) -> list[str]:
        dirs = []
        for i, factor in enumerate(factors):
            d = pathlib.Path(root, f"run{i}", "tests", "P", "TestResults")
            d.mkdir(parents=True)
            durations = {"A": 0.1 * factor, "Tiny": 0.001, "B": 0.2 * factor}
            if i != 1:
                durations["Sometimes"] = 0.5
            (d / "small-tests.trx").write_text(trx(durations))
            dirs.append(str(pathlib.Path(root, f"run{i}")))
        return dirs

    def test_keeps_each_tests_median_over_runs_for_tests_every_run_timed_at_10_ms_or_more(self):
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")

            reference = b.write_reference(str(out), "small-tests.trx", self.runs(root, [1.0, 2.0, 3.0, 4.0, 5.0]))

            self.assertEqual(set(reference), {"A", "B"})
            self.assertAlmostEqual(reference["A"], 0.3)
            self.assertAlmostEqual(reference["B"], 0.6)
            self.assertEqual(json.loads(out.read_text()), reference)

    def test_from_fewer_than_five_runs_writes_nothing(self):
        # One run is one runner's speed, not a median runner's.
        with tempfile.TemporaryDirectory() as root:
            out = pathlib.Path(root, "reference.json")

            with self.assertRaises(ValueError):
                b.write_reference(str(out), "small-tests.trx", self.runs(root, [1.0, 2.0, 3.0, 4.0]))
            self.assertFalse(out.exists())

    def test_a_run_with_no_results_writes_nothing(self):
        with tempfile.TemporaryDirectory() as root:
            dirs = self.runs(root, [1.0, 2.0, 3.0, 4.0]) + [str(pathlib.Path(root, "empty"))]
            out = pathlib.Path(root, "reference.json")

            with self.assertRaises(ValueError):
                b.write_reference(str(out), "small-tests.trx", dirs)
            self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
