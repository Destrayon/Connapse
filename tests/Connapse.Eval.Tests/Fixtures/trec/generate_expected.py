# Regenerates expected.json from trec_eval via pytrec_eval.
# pip install pytrec-eval-terrier==0.5.10 ; python generate_expected.py
# Every run list has at most 10 documents, so trec_eval's uncut recip_rank equals RR@10.
import json
import pytrec_eval

def read(path, run=False):
    data = {}
    for line in open(path):
        f = line.split()
        if run:
            data.setdefault(f[0], {})[f[2]] = float(f[4])
        else:
            data.setdefault(f[0], {})[f[2]] = int(f[3])
    return data

qrels, run = read("qrels.txt"), read("run.txt", run=True)
evaluator = pytrec_eval.RelevanceEvaluator(qrels, {"recip_rank", "ndcg_cut.10", "recall.5,10", "P.5", "success.1,3"})
out = {
    qid: {
        "MRR@10": m["recip_rank"], "nDCG@10": m["ndcg_cut_10"], "Recall@5": m["recall_5"],
        "Recall@10": m["recall_10"], "P@5": m["P_5"], "hit@1": m["success_1"], "hit@3": m["success_3"],
    }
    for qid, m in sorted(evaluator.evaluate(run).items())
}
json.dump(out, open("expected.json", "w"), indent=2)
