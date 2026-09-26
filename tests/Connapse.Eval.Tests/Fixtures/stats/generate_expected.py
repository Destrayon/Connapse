# Regenerates expected.json for the statistics ports.
# pip install scipy==1.18.0 numpy==2.4.3 statsmodels==0.15.0 ; python generate_expected.py
# The C# ports in tests/Connapse.Eval/Statistics must reproduce these values.
import json
import numpy as np
from scipy import stats
from statsmodels.stats.multitest import multipletests

out = {"versions": {"scipy": "1.18.0", "numpy": "2.4.3", "statsmodels": "0.15.0"}}

# Holm (statsmodels multipletests, method="holm") — deterministic, compared exactly.
holm_cases = [
    [0.01, 0.04, 0.03, 0.005],
    [0.5, 0.5, 0.01, 0.01, 0.2],            # ties
    [0.3, 0.6, 0.9, 0.95],                   # products above 1 → clipped
    [0.0001, 0.00011, 0.049, 0.051, 0.8, 0.02, 0.02, 0.7],
    [0.042],
]
out["holm"] = [{"p": c, "adjusted": multipletests(c, method="holm")[1].tolist()} for c in holm_cases]

# Paired sign-flip permutation test (scipy permutation_test, permutation_type="samples",
# two-sided, statistic = mean). 2^n <= n_resamples makes SciPy enumerate exactly.
def mean_stat(x, axis=-1):
    return np.mean(x, axis=axis)

perm_cases = [
    [0.5, -0.2, 0.3, 0.1, 0.25],
    [0.0, 0.0, 0.4, -0.4, 0.2, 0.2, 0.1, -0.05],                      # zeros and ties
    [0.1, 0.2, 0.15, 0.3, 0.05, 0.12, 0.2, 0.25, 0.18, 0.09, 0.11, 0.3, 0.22],  # n = 13, all positive
    [0.33, -0.33, 0.5, -0.5, 0.25, -0.25, 0.1, -0.1, 0.0, 0.0, 0.2, -0.2],      # symmetric → p = 1
]
out["permutation_exact"] = []
for d in perm_cases:
    r = stats.permutation_test((np.array(d),), mean_stat, permutation_type="samples",
                               vectorized=True, n_resamples=10000, alternative="two-sided")
    out["permutation_exact"].append({"d": d, "p": float(r.pvalue)})

# Randomized mode (n = 30 > 13): SciPy's RNG differs from .NET's, so the C# test compares
# within Monte Carlo tolerance of this high-resample reference.
rng = np.random.default_rng(7)
d30 = np.round(rng.normal(0.03, 0.2, 30), 4)
r = stats.permutation_test((d30,), mean_stat, permutation_type="samples", vectorized=True,
                           n_resamples=200000, alternative="two-sided", rng=np.random.default_rng(1))
out["permutation_randomized"] = {"d": d30.tolist(), "p": float(r.pvalue)}

# BCa bootstrap (scipy bootstrap, method="BCa", statistic = mean). The resample indices SciPy
# draws are exported so the C# port can be fed the identical resamples and compared exactly.
bca_cases = [
    [0.50, 0.17, 0.0, 0.25, 0.25, 0.0, 0.5, 0.05, 0.0, 0.17, 0.0, 0.5],
    [0.9, -0.1, 0.05, 0.0, 0.0, 0.3, -0.2, 0.15, 0.6, 0.0, 0.1, -0.05, 0.02, 0.4, 0.0, 0.0, 0.25, -0.3],  # skewed
    [0.2, 0.2, 0.2, 0.2, 0.25, 0.2, 0.2, 0.15, 0.2, 0.2],               # heavy ties
]
out["bca"] = []
for i, d in enumerate(bca_cases):
    d = np.array(d)
    B = 999
    seed = 100 + i
    idx = np.random.default_rng(seed).integers(0, len(d), (B, len(d)))
    r = stats.bootstrap((d,), mean_stat, vectorized=True, n_resamples=B, method="BCa",
                        confidence_level=0.95, rng=np.random.default_rng(seed))
    theta_b = d[idx].mean(axis=-1)
    assert np.allclose(np.sort(theta_b), np.sort(r.bootstrap_distribution)), "resample indices diverged from SciPy's"
    out["bca"].append({"d": d.tolist(), "indices": idx.tolist(),
                       "low": float(r.confidence_interval.low), "high": float(r.confidence_interval.high)})

json.dump(out, open("expected.json", "w"), separators=(",", ":"))
