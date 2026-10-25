# code-questions

- **What it tests:** whether a code search finds the files that answer four kinds of question about a codebase:
  - **definition:** where is it defined
  - **callers:** what calls it
  - **usages:** what uses it
  - **how:** how does it work

  It is the proof set for the code engine (CON-36/37). The answer keys come from compilers, never from Connapse's own parsers, so nothing grades itself.
- **Repos:** pinned in `repos.json`, each at one commit.

  | Repo | Language | Pin | License | Tracked files |
  |---|---|---|---|---|
  | Connapse | C# | `165f4ec8` (main) | MIT | 1,512 |
  | [serilog/serilog](https://github.com/serilog/serilog) | C# | v4.4.0 | Apache-2.0 | 284 |
  | [sindresorhus/ky](https://github.com/sindresorhus/ky) | TypeScript | v2.2.0 | MIT | 115 |
  | [pallets/click](https://github.com/pallets/click) | Python | 8.5.0 | BSD-3-Clause | 166 |

- **Answer keys:**
  - **C#:** `RoslynIndex` (Roslyn 5.6 `MSBuildWorkspace`) loads each solution and binds every name in every file to the symbol it means. Every project compiles except the three noted under limitations. scip-dotnet was tried first and dropped: its newest release (0.2.14) bundles Roslyn 4.4 (C# 11), which left 283 of Connapse's 1,189 files unbound, every one that uses a primary constructor.
  - **TypeScript:** scip-typescript 0.4.0.
  - **Python:** scip-python 0.6.6, run in a capped Linux container, because on Windows it fails at startup.
  - **Unit:** keys are file paths. A definition key is the declaring file, and also gives the declaration's line span. A callers key is every file that calls the symbol (a `nameof`, or a method passed as a value, names it without calling it). A usages key is every file that names it. Both carry each line. A how key is the definition's file plus the files defining what its body uses: in-repo methods and types named in its statements, not its signature, and any in-repo symbol defined in another file. Implicit `var` is not counted as a use of the type it infers.
- **Questions:** 319 in `questions.jsonl`.

  | Repo | definition | callers | usages | how |
  |---|---|---|---|---|
  | Connapse | 32 | 20 | 20 | 12 |
  | serilog | 32 | 20 | 20 | 12 |
  | ky | 32 | 12 | 20 | 3 |
  | click | 32 | 20 | 20 | 12 |

  ky is small: it has only 12 functions with callers to ask about, and 3 methods long enough for a "how" question.
  - **Wording:** each question names the symbol by its qualified name, for example "What calls the method `SyncSource.RunAsync`?", and only symbols whose qualified name is unique in their repo are used.
  - **Which symbols:** definitions in tests, docs, examples, benchmarks, migrations and generated files are left out. References from anywhere count.
- **Sampling by use:** a symbol's weight is the number of files referencing it, from 1 to 30. Each kind draws evenly across five bands (1, 2–3, 4–7, 8–15, 16–30 files) with a seeded RNG. Widely used symbols are rarer, so the final spread is 107 / 90 / 67 / 33 / 22. A "how" question needs a body of at least 8 lines that uses at least 2 in-repo symbols.
- **Split:** each question carries `"split": "tuning"` or `"heldout"`, set by the first byte of SHA-256(`seed:repo:symbol`) with seed 35, when the set is built and before any system has run. Every question about one symbol lands in the same half. The halves are 157 tuning and 162 held-out. Tune on the tuning half and report gates on the held-out half.
- **Scoring:** Recall@k is |top-k ∩ key| / min(|key|, k), averaged over questions.
- **Grep baseline** (`grep-baseline.json`, from `grep_baseline.py`). Every tracked text file is ranked by how many of its lines hold the symbol's own name as a whole word, as `grep -w -c` counts them. Held-out half:

  | Group | Questions | R@5 | R@10 | R@20 |
  |---|---|---|---|---|
  | all | 162 | 0.700 | 0.829 | 0.912 |
  | definition | 60 | 0.717 | 0.883 | 0.967 |
  | callers | 38 | 0.719 | 0.883 | 0.969 |
  | usages | 44 | 0.798 | 0.884 | 0.981 |
  | how | 20 | 0.394 | 0.443 | 0.485 |
  | crowded (>10 files hold the name) | 71 | 0.464 | 0.714 | 0.902 |
  | sparse (≤10 files hold the name) | 91 | 0.883 | 0.919 | 0.919 |

  Grep is strong here by construction: every file in a callers or usages key contains the symbol's name, so grep can only lose by ranking. On sparse names it scores about 1.0, and "+0.15 over grep" overall would mean 0.98. A gate is reachable on the crowded group (0.714, where ranking decides) and the how group (0.443).
- **Pass bar for the code engine (CON-36/37),** fixed in `gate.json` on 2026-10-24, before any run: held-out Recall@10 of at least **0.864 on crowded names** and at least **0.593 on how questions**. Both are grep + 0.15, and both must hold. This replaces a single "+0.15 over grep" across all questions.
- **Hand check:** 12 how questions, drawn with seed 35.
  - All 12 definitions and line spans are right.
  - Every symbol a key lists is named in its body.
  - In the bodies read line by line (click `_force_correct_text_reader` and `Parameter.value_from_envvar`, serilog `BatchingSink.Dispose` and `LoggerEnrichmentConfiguration.Wrap`, Connapse `SummaryJobs.GenerateAndCacheSummaryAsync`), no in-repo call is missing.
  - Fixes that came out of the check and the review, all in the generator:
    - `var` had counted as a use of the type it infers.
    - Methods of 7 lines or fewer made trivial "how" questions.
    - Signature types counted as body uses.
    - A `nameof` counted as a caller.
- **Reproduce:** this needs Git Bash, the .NET 10 SDK, Node 22 and Docker. Run it at below-normal priority:

  ```
  eval/tools/code-questions/index_repos.sh D:/tmp/codeq        # clones, then indexes one repo at a time
  python -I eval/tools/code-questions/build_questions.py D:/tmp/codeq eval/datasets/code-questions/questions.jsonl
  python -I eval/tools/code-questions/grep_baseline.py D:/tmp/codeq eval/datasets/code-questions/questions.jsonl eval/datasets/code-questions/grep-baseline.json
  ```

  Indexing takes about 5 minutes: Connapse about 2, Serilog under 1. Delete the work directory afterwards.
- **Not in `MANIFEST.json` yet:** the harness has no adapter for code questions, so the code engine's PR adds the adapter and the manifest entry.
- **Known limitations:**
  - **Razor isn't compiled into the indexed C#,** and Connapse.Web.Tests (249 errors) and Integration.Tests (9) don't compile without the generated components. A symbol named in a `.razor` or `.cshtml` file, or in a file that doesn't compile, outside the references the index already found, is never asked as a callers or usages question. Its uses there may be missing.
  - **Python bodies are approximate.** scip-python records no body ranges, so a body runs to the next definition outside it. It also files functions nested in methods under the class, so those are left out.
  - **ky has no lockfile,** so its dependencies float. They type only symbols outside the repo, which no question asks about.
  - **Keys are files, not lines.** The `locations` and `lines` fields allow line-level judging later.
  - **Long methods make large how keys,** up to about 20 files. Recall@10 caps the denominator at 10.
