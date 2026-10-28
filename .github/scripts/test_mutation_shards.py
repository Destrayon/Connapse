import os
import tempfile
import unittest

import mutation_shards
from test_mutation_score import mutant, report

FILTERED = f"Removed by {mutation_shards.MUTATE_FILTER}"


def plan_of(*shards):
    return {"mode": "partial", "shards": [{"index": i, "files": list(fs)} for i, fs in enumerate(shards)]}


class SplitTests(unittest.TestCase):
    def test_heavy_files_spread_so_shards_are_close_to_even(self):
        shards = mutation_shards.split({"A.cs": 10, "B.cs": 6, "C.cs": 5, "D.cs": 1}, 2)
        self.assertEqual(shards, [["A.cs", "D.cs"], ["B.cs", "C.cs"]])

    def test_every_file_lands_in_exactly_one_shard(self):
        weight = {f"F{i}.cs": i % 3 for i in range(20)}
        shards = mutation_shards.split(weight, 4)
        self.assertEqual(sorted(f for s in shards for f in s), sorted(weight))


class PlanTests(unittest.TestCase):
    def test_a_small_partial_run_is_one_shard_with_no_mutate_filter(self):
        p = mutation_shards.plan(["A.cs", "B.cs"], {"A.cs": 30, "B.cs": 0}, "partial", 4, 150)
        self.assertEqual([(s["files"], s["mutate"]) for s in p["shards"]], [(["A.cs", "B.cs"], [])])

    def test_a_partial_run_gets_a_shard_per_share_of_mutants_up_to_the_cap(self):
        weight = {f"F{i}.cs": 100 for i in range(5)}
        self.assertEqual(len(mutation_shards.plan(list(weight), weight, "partial", 4, 150)["shards"]), 4)
        self.assertEqual(len(mutation_shards.plan(list(weight), weight, "partial", 8, 150)["shards"]), 4)

    def test_a_full_run_always_uses_every_shard(self):
        p = mutation_shards.plan(["A.cs", "B.cs", "C.cs"], {"A.cs": 1, "B.cs": 1, "C.cs": 1}, "full", 4, 150)
        self.assertEqual(len(p["shards"]), 3)  # never more shards than files
        self.assertEqual(p["shards"][0]["mutate"], [f"**/{f}" for f in p["shards"][0]["files"]])


class WeightTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        with open(os.path.join(self.dir, "New.cs"), "w") as f:
            f.write("x\n" * 80)

    def weights(self, mode, changed=()):
        base = report({"A.cs": [mutant("Killed", line=1, covered_by=["t1"]), mutant("Survived", line=2, covered_by=["t2"]),
                                mutant("CompileError", line=3), mutant("NoCoverage", line=4)]},
                      tests={"tests/ATests.cs": ["t1"], "tests/BTests.cs": ["t2"]})
        return mutation_shards.weights(["A.cs", "New.cs"], self.dir, "src/App", mode, base, set(changed), "/repo")

    def test_a_full_run_counts_every_mutant_the_baseline_tested(self):
        self.assertEqual(self.weights("full")["A.cs"], 2)

    def test_a_changed_file_counts_all_its_tested_mutants(self):
        self.assertEqual(self.weights("partial", ["src/App/A.cs"])["A.cs"], 2)

    def test_an_unchanged_file_counts_only_mutants_a_changed_test_covers(self):
        self.assertEqual(self.weights("partial", ["tests/ATests.cs"])["A.cs"], 1)
        self.assertEqual(self.weights("partial")["A.cs"], 0)

    def test_a_file_the_baseline_lacks_is_estimated_from_its_length(self):
        self.assertEqual(self.weights("partial")["New.cs"], 10)


class MergeTests(unittest.TestCase):
    def test_each_file_comes_from_the_shard_that_owns_it(self):
        s0 = report({"A.cs": [mutant("Killed")], "B.cs": [mutant("Ignored", reason=FILTERED)]}, tests={"t/T.cs": ["t1"]})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED)], "B.cs": [mutant("Survived")]}, tests={"t/T.cs": ["t1"]})
        merged, warnings = mutation_shards.merge(plan_of(["A.cs"], ["B.cs"]), [s0, s1])
        statuses = {p.rsplit("/", 1)[-1]: [m["status"] for m in s["mutants"]] for p, s in merged["files"].items()}
        self.assertEqual(statuses, {"A.cs": ["Killed"], "B.cs": ["Survived"]})
        self.assertEqual(merged["projectRoot"], "/repo/src/App")
        self.assertEqual(list(merged["testFiles"]), ["/repo/t/T.cs"])
        self.assertEqual(warnings, [])

    def test_a_file_its_owner_filtered_out_fails(self):
        # The owner's --mutate pattern missed the file, so nobody tested it.
        s0 = report({"A.cs": [mutant("Ignored", reason=FILTERED, line=1), mutant("CompileError", line=2)]})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED)]})
        with self.assertRaisesRegex(ValueError, "filtered out A.cs"):
            mutation_shards.merge(plan_of(["A.cs"], []), [s0, s1])

    def test_a_file_missing_from_its_owners_report_fails(self):
        s0 = report({})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED)]})
        with self.assertRaisesRegex(ValueError, "missing from shard 0"):
            mutation_shards.merge(plan_of(["A.cs"], []), [s0, s1])

    def test_a_file_in_no_plan_fails(self):
        with self.assertRaisesRegex(ValueError, "in no shard's plan"):
            mutation_shards.merge(plan_of(["A.cs"], []), [report({"A.cs": [mutant("Killed")]}), report({"Z.cs": []})])

    def test_a_file_another_shard_also_tested_only_warns(self):
        s0 = report({"A.cs": [mutant("Killed")]})
        s1 = report({"A.cs": [mutant("Survived")]})
        merged, warnings = mutation_shards.merge(plan_of(["A.cs"], []), [s0, s1])
        self.assertEqual([m["status"] for s in merged["files"].values() for m in s["mutants"]], ["Killed"])
        self.assertEqual(len(warnings), 1)

    def test_one_shard_keeps_every_file(self):
        merged, _ = mutation_shards.merge(plan_of(["A.cs"]), [report({"A.cs": [mutant("Killed")], "B.cs": []})])
        self.assertEqual(len(merged["files"]), 2)

    def test_a_missing_shard_report_fails(self):
        with self.assertRaisesRegex(ValueError, "1 report"):
            mutation_shards.merge(plan_of(["A.cs"], []), [report({})])


if __name__ == "__main__":
    unittest.main()
