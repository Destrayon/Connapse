using Connapse.Eval.Judges;

Console.WriteLine("Connapse summary eval harness — manual run.");
Console.WriteLine();
Console.WriteLine("To run the eval against a real corpus:");
Console.WriteLine("  1. Configure an LLM provider in your local appsettings or env vars.");
Console.WriteLine("  2. Start Docker dev services (docker compose up -d).");
Console.WriteLine("  3. Populate tests/Connapse.Eval/Corpora/<name>/ with seed documents.");
Console.WriteLine("  4. Wire this Program.cs to: (a) ingest each corpus, (b) wait for rollup,");
Console.WriteLine("     (c) fetch Container.Summary, (d) call SummaryJudge.EvaluateAsync,");
Console.WriteLine("     (e) write results to docs/eval/baseline-scores-<date>.md.");
Console.WriteLine();
Console.WriteLine("v1 ships the harness scaffold + SummaryJudge class. The end-to-end runner");
Console.WriteLine("requires Docker + LLM provider config and is left as a manual workflow.");
return 0;
