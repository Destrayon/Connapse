"""Tests for shard_tests.py (#843). Run with: python3 -m unittest discover -s .github/scripts -p "test_*.py"."""
import datetime
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import shard_tests as s  # noqa: E402

T0 = datetime.datetime(2026, 10, 24, 12, 0, 0)


def at(seconds: float) -> datetime.datetime:
    return T0 + datetime.timedelta(seconds=seconds)


class Estimate(unittest.TestCase):
    def test_a_timed_class_keeps_its_timing(self):
        self.assertEqual(s.estimate({"A": 10}, {"A": 42.0}), {"A": 42.0})

    def test_a_new_class_counts_its_tests_at_the_median_rate(self):
        # Rates 1, 2 and 10 seconds per test: the median, 2, not the mean, 4.33.
        timings = {"A": 10.0, "B": 20.0, "C": 100.0}
        seconds = s.estimate({"A": 10, "B": 10, "C": 10, "New": 5}, timings)
        self.assertEqual(seconds["New"], 10.0)

    def test_with_no_timings_every_test_counts_one_second(self):
        self.assertEqual(s.estimate({"A": 3, "B": 7}, {}), {"A": 3.0, "B": 7.0})


class Plan(unittest.TestCase):
    def test_classes_go_longest_first_to_the_least_loaded_shard(self):
        timings = {"A": 50.0, "B": 40.0, "C": 30.0, "D": 20.0, "E": 10.0}
        assigned = s.plan({c: 1 for c in timings}, set(), timings, 2)
        self.assertEqual(assigned, [["A", "D", "E"], ["B", "C"]])  # 80 s and 70 s, not round robin's 90 and 60

    def test_container_classes_are_spread_one_per_shard_before_the_rest(self):
        timings = {"N.Sftp": 5.0, "N.Azure": 5.0, "N.Big": 100.0}
        assigned = s.plan({c: 1 for c in timings}, {"Sftp", "Azure"}, timings, 3)
        self.assertEqual(sorted(len([c for c in shard if c.endswith(("Sftp", "Azure"))]) for shard in assigned), [0, 1, 1])
        self.assertIn(["N.Big"], assigned)

    def test_every_class_lands_in_exactly_one_shard(self):
        counts = {f"N.C{i}": i % 7 + 1 for i in range(40)}
        assigned = s.plan(counts, {"C3"}, {"N.C1": 9.0}, 3)
        flat = [c for shard in assigned for c in shard]
        self.assertEqual(sorted(flat), sorted(counts))

    def test_ties_break_by_name_so_every_shard_computes_the_same_plan(self):
        counts = {"B": 1, "A": 1, "C": 1}
        self.assertEqual(s.plan(counts, set(), {}, 2), s.plan(dict(reversed(list(counts.items()))), set(), {}, 2))
        self.assertEqual(s.plan(counts, set(), {}, 2), [["A", "C"], ["B"]])


class ByName(unittest.TestCase):
    def test_name_order_deals_container_classes_first_then_the_rest_round_robin(self):
        counts = {"N.A": 1, "N.B": 1, "N.C": 1, "N.Sftp": 1}
        self.assertEqual(s.by_name(counts, {"Sftp"}, 2), [["N.Sftp", "N.B"], ["N.A", "N.C"]])


class Filter(unittest.TestCase):
    def test_a_named_shard_selects_its_classes(self):
        self.assertEqual(s.shard_filter([["N.A"], ["N.B"], ["N.C"]], 1, "Tier"), "(Tier)&(FullyQualifiedName~N.A.)")

    def test_the_last_shard_runs_everything_the_others_do_not_name(self):
        self.assertEqual(s.shard_filter([["N.A"], ["N.B"], ["N.C"]], 3, "Tier"),
                         "(Tier)&(FullyQualifiedName!~N.A.&FullyQualifiedName!~N.B.)")


class Timings(unittest.TestCase):
    def test_time_between_tests_belongs_to_the_test_before_the_gap(self):
        results = [(at(0), at(1), "A"), (at(5), at(6), "B"), (at(6), at(10), "A")]
        self.assertEqual(s.attributed_seconds(results), {"A": 9.0, "B": 1.0})

    def test_a_runs_classes_add_up_to_its_span(self):
        results = [(at(0), at(2), "A"), (at(3), at(4), "B"), (at(10), at(12), "C"), (at(12.5), at(20), "A")]
        self.assertEqual(sum(s.attributed_seconds(results).values()), 20.0)

    def test_a_trx_file_is_read_into_per_class_seconds(self):
        trx = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="1" startTime="2026-10-24T12:00:00.0000000+00:00" endTime="2026-10-24T12:00:02.0000000+00:00" />
    <UnitTestResult testId="2" startTime="2026-10-24T12:00:03.0000000+00:00" endTime="2026-10-24T12:00:04.5000000+00:00" />
  </Results>
  <TestDefinitions>
    <UnitTest id="1"><TestMethod className="N.A" name="One" /></UnitTest>
    <UnitTest id="2"><TestMethod className="N.B" name="Two" /></UnitTest>
  </TestDefinitions>
</TestRun>"""
        with tempfile.TemporaryDirectory() as d:
            pathlib.Path(d, "shard-1.trx").write_text(trx, encoding="utf-8")
            self.assertEqual(s.make_timings(f"{d}/*.trx"), {"N.A": 3.0, "N.B": 1.5})


class Listing(unittest.TestCase):
    def test_tests_are_counted_per_class_and_custom_names_are_left_out(self):
        lines = ["    N.A.One", "    N.A.Two(x: 1)", "    N.B.Three", "    A display name with spaces"]
        self.assertEqual(s.count_classes(lines), {"N.A": 2, "N.B": 1})


if __name__ == "__main__":
    unittest.main()
