#!/usr/bin/env bash
# How many IDocumentParser implementations each ast-grep pattern finds, vs a ripgrep regex.
cd /e/tmp/con-142/corpus
for p in 'class $N : IDocumentParser' 'public class $N : IDocumentParser { $$$ }' 'public class $N : IDocumentParser' 'public sealed class $N : IDocumentParser { $$$ }'; do
  n=$(/e/tmp/con-142/bin/ast-grep.exe run --lang csharp -p "$p" --json=stream . 2>/dev/null | wc -l)
  echo "ast-grep [$p] -> $n"
done
echo "rg class-with-base:"
/e/tmp/con-142/bin/rg.exe -n 'class \w+(\([^)]*\))?\s*:\s*[^{]*\bIDocumentParser\b' --type cs .
