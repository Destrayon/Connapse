import os
import tempfile
import unittest

import mutation_shards
from test_mutation_score import mutant, report

FILTERED = f"Removed by {mutation_shards.MUTATE_FILTER}"


def plan_of(*shards):
    return {"mode": "partial", "shards": [{"index": i, "files": list(fs)} for i, fs in enumerate(shards)]}


def cut_plan():
    """A.cs cut in two, one part per shard."""
    return {"mode": "partial", "shards": [
        {"index": 0, "files": [], "parts": [{"file": "A.cs", "span": "{0..50}"}]},
        {"index": 1, "files": [], "parts": [{"file": "A.cs", "span": "{50..2147483647}"}]}]}


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

    def test_a_file_heavier_than_a_quarter_shard_is_cut_into_parts_across_shards(self):
        weight = {"Big.cs": 200, "A.cs": 100, "B.cs": 100, "C.cs": 0}
        divide = lambda f, n: [(0, 10, 100), (10, 2**31 - 1, 100)] if f == "Big.cs" else [(0, 2**31 - 1, weight[f])]
        p = mutation_shards.plan(list(weight), weight, "partial", 4, 100, divide)
        self.assertEqual([s["estimate"] for s in p["shards"]], [100, 100, 100, 100])
        self.assertNotIn("Big.cs", [f for s in p["shards"] for f in s["files"]])
        self.assertEqual(sorted(m for s in p["shards"] for m in s["mutate"] if "Big" in m),
                         ["**/Big.cs{0..10}", "**/Big.cs{10..2147483647}"])
        self.assertEqual(sorted(x["span"] for s in p["shards"] for x in s["parts"]), ["{0..10}", "{10..2147483647}"])

    def test_one_shard_cuts_nothing(self):
        p = mutation_shards.plan(["Big.cs"], {"Big.cs": 90}, "partial", 4, 100, lambda f, n: 1 / 0)
        self.assertEqual(p["shards"][0]["mutate"], [])


CLASS = """namespace App;

public sealed class A
{
    private const string Open = "{ \\" {";
    private const char Brace = '{';

    public int One(int x)
    {
        var s = $"{x} {{ {(x > 0 ? "}" : "")}";

        return x + 1; // }
    }

    public int Two =>
        /* } */

        2;

    public void Three<T>() where T : class
    {
        int y = 3;

        _ = y;
    }
}

public sealed record B(int X);
"""


def lines_of(text, cuts):
    return [line for _, line in cuts]


class CutPointTests(unittest.TestCase):
    def test_cuts_fall_only_between_members_and_top_level_declarations(self):
        # Line 7 follows the fields, 14 One, 19 Two and 27 the class; none fall inside a body or expression,
        # whatever braces its literals and comments hold, and a `where T : class` method is no type.
        self.assertEqual(lines_of(CLASS, mutation_shards.cut_points(CLASS)), [2, 7, 14, 19, 27])

    def test_an_offset_counts_utf16_code_units_as_roslyn_does(self):
        text = 'namespace App;\n// \U0001F600\n\nclass A { }\n'
        [(offset, line)] = [c for c in mutation_shards.cut_points(text) if c[1] == 3]
        self.assertEqual(offset, len("namespace App;\n// ") + 2 + 1)

    def test_a_crlf_file_counts_both_characters(self):
        text = CLASS.replace("\n", "\r\n")
        self.assertEqual(lines_of(text, mutation_shards.cut_points(text)), [2, 7, 14, 19, 27])
        self.assertTrue(all(text[o - 2:o] == "\r\n" for o, _ in mutation_shards.cut_points(text)))


class PartsTests(unittest.TestCase):
    def test_parts_cover_the_file_end_to_end_with_about_even_costs(self):
        costs = [(9, 1), (10, 1), (16, 1), (17, 1), (22, 1), (24, 1)]
        parts = mutation_shards.parts(CLASS, costs, 3)
        self.assertEqual(parts[0][0], 0)
        self.assertEqual(parts[-1][1], 2**31 - 1)
        self.assertTrue(all(a[1] == b[0] for a, b in zip(parts, parts[1:])))
        self.assertEqual([c for _, _, c in parts], [2, 2, 2])

    def test_a_file_with_no_cut_is_one_part(self):
        self.assertEqual(mutation_shards.parts("class A { int X() { return 1; } }", [(1, 5)], 3),
                         [(0, 2**31 - 1, 5)])


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

    def test_a_timeout_counts_as_many_mutants(self):
        base = report({"A.cs": [mutant("Killed", line=1), mutant("Timeout", line=2)]})
        w = mutation_shards.weights(["A.cs"], self.dir, "src/App", "full", base, set(), "/repo")
        self.assertEqual(w["A.cs"], 1 + mutation_shards.TIMEOUT_COST)

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

    def test_a_cut_files_mutants_come_from_the_shard_whose_part_kept_each(self):
        s0 = report({"A.cs": [mutant("Killed", line=1), mutant("Ignored", reason=FILTERED, line=9),
                              mutant("CompileError", line=12)]})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED, line=1), mutant("Survived", line=9),
                              mutant("CompileError", line=12)]})
        merged, _ = mutation_shards.merge(cut_plan(), [s0, s1])
        self.assertEqual([m["status"] for s in merged["files"].values() for m in s["mutants"]],
                         ["Killed", "Survived", "CompileError"])

    def test_a_cut_files_mutant_no_part_kept_fails(self):
        s0 = report({"A.cs": [mutant("Killed", line=1), mutant("Ignored", reason=FILTERED, line=9)]})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED, line=1), mutant("Ignored", reason=FILTERED, line=9)]})
        with self.assertRaisesRegex(ValueError, "kept the mutant at line 9"):
            mutation_shards.merge(cut_plan(), [s0, s1])

    def test_a_cut_files_mutant_two_parts_tested_fails(self):
        s0 = report({"A.cs": [mutant("Killed", line=1)]})
        s1 = report({"A.cs": [mutant("Survived", line=1)]})
        with self.assertRaisesRegex(ValueError, "several shards"):
            mutation_shards.merge(cut_plan(), [s0, s1])

    def test_shards_that_mutated_a_cut_file_differently_fail(self):
        s0 = report({"A.cs": [mutant("Killed", line=1)]})
        s1 = report({"A.cs": [mutant("Ignored", reason=FILTERED, line=2)]})
        with self.assertRaisesRegex(ValueError, "differently"):
            mutation_shards.merge(cut_plan(), [s0, s1])

    def test_a_cut_file_missing_from_a_part_owners_report_fails(self):
        s0 = report({"A.cs": [mutant("Killed", line=1)]})
        with self.assertRaisesRegex(ValueError, "missing from shard 1's report, which owns a part"):
            mutation_shards.merge(cut_plan(), [s0, report({})])

    def test_a_missing_shard_report_fails(self):
        with self.assertRaisesRegex(ValueError, "1 report"):
            mutation_shards.merge(plan_of(["A.cs"], []), [report({})])


if __name__ == "__main__":
    unittest.main()
