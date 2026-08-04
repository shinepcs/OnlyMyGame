# Rule generation release evaluation

`scripts/evaluate-rules.py` measures the four AI release gates in the PRD with
100 deterministic representative world states:

- first-response validity at least 95%;
- validity including the server's one repair attempt at least 99%;
- successful-generation client p95 latency at most 8 seconds;
- at least 80 distinct semantic rule-graph signatures across the 100 runs.

The evaluator uses only the Python standard library. It never reads an OpenAI
key. It obtains one anonymous game session from the configured OnlyMyGame API
and uses one ephemeral `runId` for all 100 sequential cases.

## Safe dry run

Run the dataset and signature self-check without an API URL, confirmation, file
write, network request, or paid model call:

```sh
python3 scripts/evaluate-rules.py --dry-run
```

The check requires exactly 100 snapshots, 217 unique radius-8 hexes and three
factions per snapshot, varied resources/action statistics/active rules/victory
contracts, deterministic dataset hashing, and these signature invariants:

- generated IDs and presentation text do not change a signature;
- a semantic effect change does change a signature.

Fake in-memory HTTP bodies also verify that connection resets, TLS EOF during
response close, incomplete bodies, HTTP-error close failures, and client
timeouts become safe per-case categories. Generic malformed/overlong HTTP
status or header lines are covered as network failures as well. These checks do
not resolve DNS or open a socket.

## Live evaluation

Live mode is deliberately guarded twice. Supply an explicit HTTPS API origin
and set the exact confirmation immediately before the command:

```sh
ONLYMYGAME_EVAL_CONFIRM=RUN_100_PAID_REQUESTS \
  python3 scripts/evaluate-rules.py \
  --api-url https://office.example:10433 \
  --output artifacts/release-evaluation/rules-evaluation.json
```

The confirmation authorizes exactly 100 sequential `/v1/rules/generate`
requests. Each request can cause one initial paid model call and, when needed,
one server-side repair call, so the upper bound is 200 upstream model calls.
Do not run it without the corresponding API quota and cost approval.

Before issuing a session or any paid request, the evaluator requires `/health`
to report all of the following:

- healthy, configured service and database;
- `apiVersion: v1`;
- `compatibilityVersion: rules-v2-strict-2026-08`;
- `limits.perClientDailyAttempts >= 100`;
- `limits.globalDailyAttempts >= 100`.

The output parent and target are checked before `/health`. A target-specific
`O_EXCL` reservation and same-directory hard-link probe must succeed first.
Existing targets are refused immediately. A complete `0600` temporary report is
fsynced and then published through a no-overwrite hard link, so a file created by
another process during evaluation is never replaced. Locks and temporary files
are removed on normal failure or interruption; the final path is not used as an
empty placeholder.

The advertised limits are configured daily caps, not remaining quota. A prior
run from the same client can therefore still produce `429` responses. The
evaluator records `429`, `503`, timeout, TLS, network, invalid-JSON, missing
attempt-header, and client-validation failures as safe category codes and
continues until all 100 cases have been attempted. It never retries at the
client layer; the server's `X-OnlyMyGame-Generation-Attempts: 1|2` header is the
only source for first-response versus repaired-response classification.

## Results and data minimization

The JSON report contains thresholds, aggregate counts/rates, allowlisted health
metadata, and these per-case fields only: case index, deterministic seed, turn,
HTTP status, generation-attempt count, client latency, parsed numeric server latency,
validity booleans, a SHA-256 semantic graph signature, and safe error codes.

It does **not** store raw prompts, snapshots, responses, generated announcement
text or IDs, API response bodies, session tokens, or the ephemeral `runId`.
Semantic signatures omit request/generated IDs, Korean summary, names,
descriptions, and world cues. Server-owned turn fields are normalized to turn
offsets before hashing.

Exit status is `0` only when all four metric gates pass and all 100 cases finish.
Exit status `3` means the evaluation completed and at least one gate failed.
Preflight, safety, dataset, transport-setup, and report-write failures return
`2`; interruption returns `130`.
