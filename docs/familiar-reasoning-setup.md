# Configuring the Familiar's reasoning provider

The Familiar page works with **no provider configured and no credential present**. It renders the
project, a deterministic summary of what is recorded, and what that summary cannot see; a message you
send is durably saved and answered with one honest sentence explaining that no provider is set up.

That is the default, and it is a supported way to run this application indefinitely.

Configuring a provider adds conversation on top of that floor. It never replaces it: every failure
state below still leaves the page fully usable, with the deterministic summary intact.

---

## What a provider can and cannot do

Worth reading before you point this at anything, because it is the same on every provider:

- **No tools are ever declared.** A model with no tools cannot request one, so there is no execution
  channel regardless of what a reply says.
- **Model output is inert data.** It is stored and rendered with HTML encoding — no markdown
  rendering, no autolinking. A URL a model writes is characters; a command it writes is characters.
- **The Familiar can propose exactly two things** — creating a task, or starting a Planner session on
  a task that already exists — and it can never perform either. A human confirms, and every gate is
  re-checked inside the confirming database transaction.
- **No hidden reasoning is stored.** There is no column for a prompt, a thinking block, a raw payload
  or a provider exception, so nothing can write one.
- **No provider text reaches you.** Failures are reported in this application's own fixed wording;
  an error body or exception message is never read, logged, persisted or displayed.

---

## Option 1 — no provider (the default)

Nothing to do. `Familiar:Reasoning:Provider` unset means the unconfigured provider, and the page says
so plainly.

---

## Option 2 — an OpenAI-compatible endpoint

One provider covers both a model on your own machine and a hosted endpoint, because they speak the
same `/v1/chat/completions` shape. Switching between them is a base address, not a code change.

```jsonc
// appsettings.Development.json — note: no API key here, ever
{
  "Familiar": {
    "Reasoning": {
      "Provider": "OpenAiCompatible",
      "OpenAiCompatible": {
        "BaseAddress": "http://127.0.0.1:11434/v1",
        "Model": "qwen3:4b",
        "DisplayName": "Local model",
        "ApiKeyVariable": null,
        "MaxOutputTokens": 2048,
        "TimeoutSeconds": 300,
        "UseStructuredOutput": true
      }
    }
  }
}
```

| Setting | What it does |
|---|---|
| `BaseAddress` | Endpoint root, **without** `/chat/completions`. |
| `Model` | The model id exactly as that endpoint names it. |
| `DisplayName` | What is recorded on each reply and shown beside it. Use something you will recognise months later — `Groq (llama-3.3-70b)` beats `OpenAiCompatible`. |
| `ApiKeyVariable` | The **name of an environment variable** holding the key, or `null` for endpoints that need none. |
| `TimeoutSeconds` | Default 300. Generous on purpose — see the hardware note below. |
| `UseStructuredOutput` | Sends the reply schema so decoding is constrained. Leave on; turn off only if an endpoint rejects `response_format`. |

**The key never goes in configuration.** `ApiKeyVariable` names a variable; the value is read from
the environment at startup. There is no property on the options type that can hold a key, so one
cannot be committed to `appsettings.json` or printed by a configuration dump.

```bash
export GROQ_API_KEY="…"          # matches "ApiKeyVariable": "GROQ_API_KEY"
```

### Running a model locally

Any of these serve an OpenAI-compatible endpoint:

| Runtime | Typical base address |
|---|---|
| Ollama | `http://127.0.0.1:11434/v1` |
| llama.cpp (`llama-server`) | `http://127.0.0.1:8080/v1` |
| vLLM | `http://127.0.0.1:8000/v1` |
| LM Studio | `http://127.0.0.1:1234/v1` |

With Ollama:

```bash
ollama serve
ollama pull qwen3:4b
```

> **Hardware note — check this before you invest an afternoon.** Prompt processing dominates: the
> Familiar sends a bounded project snapshot (up to ~6,000 tokens) with every question, so the cost is
> reading the prompt, not writing the answer.
>
> **Your CPU needs AVX2.** Anything from roughly 2013 onward has it. On a pre-AVX machine
> `llama.cpp` falls back to a path around ten times slower — measured on a 2010 Xeon E5645
> (SSE4.2 only), prefill ran at **3.6 tokens/sec**, which is over half an hour per question. That is
> not a tuning problem and no smaller model fixes it.
>
> Check with `grep -o -m1 avx2 /proc/cpuinfo`. With AVX2 and a 4B model, expect answers in
> tens of seconds; with any modest GPU, seconds.

### Using a hosted endpoint

The same provider, a different base address. Hosted open models cost a small fraction of a penny per
question at this request size; proxies such as OpenRouter also expose Claude, Gemini and others
through the same shape, so you can compare models by changing one string.

```jsonc
{
  "Familiar": {
    "Reasoning": {
      "Provider": "OpenAiCompatible",
      "OpenAiCompatible": {
        "BaseAddress": "https://api.groq.com/openai/v1",
        "Model": "llama-3.3-70b-versatile",
        "DisplayName": "Groq (llama-3.3-70b)",
        "ApiKeyVariable": "GROQ_API_KEY"
      }
    }
  }
}
```

---

## What each failure looks like

Every one of these leaves the page fully usable and the deterministic summary intact. Your message is
saved before the provider is ever contacted, so nothing you typed is lost to a provider problem.

| Code | Shown to you | Usually means |
|---|---|---|
| `provider-not-configured` | No reasoning provider is configured, so I can only show you what is recorded. | `Provider` unset. |
| `provider-unavailable` | The reasoning provider could not be reached. Your message was saved and nothing was changed. | Endpoint not running, wrong address, or a 5xx. |
| `provider-unauthenticated` | …rejected this application's credentials. That is a server configuration problem, not something you did. | Key missing, wrong, or out of credit. |
| `provider-timeout` | …did not answer within *N* seconds. Your message was saved — try again. | Usually a local model still reading the prompt. Raise `TimeoutSeconds`. |
| `provider-rate-limited` | …is rate limiting this application right now. | Free-tier limits. |
| `provider-response-unusable` | …returned a response this application could not use. | Endpoint rejected `response_format`, or the model produced unusable JSON. Try `UseStructuredOutput: false`. |
| `provider-declined` | …declined to answer that message. | A safety classifier refused. |
| `snapshot-too-large` | This project is larger than I can summarise safely, so I did not send it. The summary above is complete and accurate. | The project exceeded the budget after every documented reduction. Nothing was sent anywhere. |

---

## Bounds

Fixed, published, and enforced before anything is transmitted:

| Bound | Value |
|---|---|
| User message | 4,000 characters |
| Conversation history sent | 10 turns, trimmed further by measurement |
| Project snapshot | 24,000 characters, then refused rather than truncated |
| Whole request envelope | 40,000 characters, re-measured immediately before sending |

The envelope is measured with the same serializer that produced it, so what was measured and what is
sent cannot disagree. If a project does not fit, it is refused and the page says so — a quietly
truncated project would answer about a different project than the one on screen.
