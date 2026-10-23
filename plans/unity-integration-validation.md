# Unity integration validation - 2026-10-23

## Environment and method

Unity 2022.3.50f1, Megacity Metro, locally referenced AgentCore 1.17.0.
Tests ran inside the already-open Unity Editor via TestRunnerApi (not an offline substitute).
The test project manifest now includes `com.agentcore.unity` in `testables`.
Temporary Editor validation scripts are removed after validation; results remain in the
project's `Temp/AgentCoreValidation/` directory (temporary, not version controlled).
No scene assets were intentionally modified. No credentials were printed or added to source.

## Results

- Initial EditMode run: 190 total, 184 passed, 6 failed.
- Repaired planning-marker newline handling and five tests missing expected Unity error logs.
- Final run: 197 total, 197 passed, 0 failed. Added coverage for native discovery headers,
  truncated/cancelled/complete Anthropic SSE, OpenAI usage-only SSE, signed-thinking
  persistence, and a marker newline split across chunks.
- Existing chat window renders the cache-hit/cost summary. Full interactive AgentLoop
  orchestration was not exercised by the direct client probes.
- Live `aws Profile` / `claude-sonnet-5`: native model discovery returned 10 models.
- Live three-round probe: streaming tool call -> persisted assistant and simulated echo
  result -> non-streaming reply -> streaming continuation all succeeded.
- Initial cold stream reported cache writes; warm repeat reported 100.0% cache hits.
  Follow-up requests reported 99.4% and 99.9% input cache hits, with OK / DONE responses.
  These are provider-reported usage ratios for this synthetic stable-prefix probe,
  not a guarantee for normal sessions or an independent billing measurement.
- The tested gateway returned no replayable thinking blocks. Signature persistence and
  mapping pass synthetic tests, but real signed-thinking replay remains unverified.
- Only the tested aws Profile protocol/auth fields are set to anthropic/bearer;
  active profile, credentials, endpoint, model and other overrides remain unchanged.

## Fixes discovered during integration

1. Preserve initial-line-break stripping across planning marker chunk boundaries.
2. Declare expected error logs in invalid-JSON tests so Unity does not report false failures.
3. Reject truncated Anthropic streams and propagate cancellation, preventing incomplete
   tool calls from being returned as successful messages.
4. Route model discovery/connection checks through profile-aware URL/version/auth logic.
   Connection status explicitly describes model-list connectivity, not generation success.
5. Preserve protocol, dialect, TTL, auth and fallback list when duplicating profiles.

## External blockers and remaining validation

- Default DeepSeek service: 20-second bounded request timed out.
- Local WAN Test DeepSeek gateway: HTTP 500 `new_api_error` / `do_request_failed`.
  No evidence that these failures are caused by request serialization; do not mark live
  DeepSeek reasoning echo or cache hit rates as validated until upstream connectivity recovers.
- No available live OpenAI/OpenRouter endpoint was tested. OpenAI usage and dialect
  behavior have synthetic coverage only.
- Real signed-thinking replay, vision requests, utility/fallback failover and a full
  chat-window agent run remain separate acceptance checks; this report does not claim
  those live scenarios passed.

## Repeat

Open Window > General > Test Runner, select EditMode and AgentCore.Tests.Editor.
For service retests, use the configured endpoint and its own credentials; do not place
keys in scripts. Native gateways require explicit Anthropic API Format when host-based
Auto detection cannot identify them. Use the gateway's required authentication scheme.
