namespace Engram.Core;

public static class DefaultConfig
{
    public static string Content => $"""
        [embedding]
        # The vector lane is optional. "none" is a supported configuration, not a degraded
        # one — recall works lexically without it. Turning it on is a tradeoff in disk,
        # memory and startup time, so it is a choice rather than a default.
        #
        #   none           lexical recall only
        #   local          a model Engram runs itself, from the list below
        #   openai-compat  anything serving POST /v1/embeddings — LM Studio, llama.cpp's
        #                  server, vLLM, OpenAI, Voyage, most hosted providers
        #   ollama         Ollama's native batch endpoint
        #
        # Note: Anthropic publishes no embeddings API. Claude cannot be a provider here.
        provider = "none"

        # -- local --
        # Smallest to largest. Width comes from the model, so `dim` is not needed:
        #   all-minilm-l6-v2       384d   ~25 MB   256-token window, English
        #   nomic-embed-text-v1.5  768d  ~140 MB  8k-token window, English
        #   qwen3-embedding-0.6b  1024d  ~610 MB  32k-token window, 100+ languages
        model = "nomic-embed-text-v1.5"

        # "local" loads the model into Engram itself, through llama.cpp. Nothing else to install:
        # the engine ships with the binary and `engram init` downloads the weights. Metal is used
        # on Apple silicon automatically.

        # -- openai-compat / ollama --
        # endpoint = "http://localhost:1234/v1"   # LM Studio, vLLM, llama.cpp server
        # endpoint = "http://localhost:11434"     # Ollama
        # model = "nomic-embed-text-v1.5"         # whatever the endpoint calls it
        # dim = 768                               # the width it returns; `engram embed --probe` asks
        # api_key_env = "OPENAI_API_KEY"          # the NAME of the variable, never the key
        # timeout_seconds = 60

        # -- shared --
        max_batch = 16

        [retrieval]
        default_budget_tokens = 500
        seed_k = 32
        graph_hops = 2
        recency_half_life_days = 45

        [backup]
        enabled = true
        interval_minutes = 60         # a ceiling: a snapshot is taken only if facts changed too
        journal = true                # also write facts.jsonl, which replays into any later schema
        keep_hourly = 24
        keep_daily = 7
        keep_weekly = 4

        [indexing]
        auto_index_on_session_start = true
        max_sync_index_ms = 1500      # beyond this, indexing continues async

        # Where there is a git checkout, git decides what belongs to the repo:
        # tracked files plus untracked ones that are not ignored. That already
        # excludes build output, node_modules, caches and temp files, per every
        # nested .gitignore and your global ignore file — decisions you already
        # made, rather than a staler copy of them kept here.
        use_git = true

        # Applied on top of that, and used alone when there is no checkout —
        # which is where they earn their keep, since a committed file can still
        # be junk to index but git will not say so. Covers several ecosystems on
        # purpose: a list that only knows its author's languages walks a Python
        # .venv or a Swift .build and finds tens of thousands of files.
        ignore = [
          "**/.git/**",
          "**/bin/**", "**/obj/**",
          "**/node_modules/**", "**/.next/**",
          "**/.venv/**", "**/venv/**", "**/__pycache__/**",
          "**/*.egg-info/**", "**/.mypy_cache/**", "**/.pytest_cache/**",
          "**/.build/**", "**/DerivedData/**", "**/Pods/**",
          "**/target/**", "**/vendor/**",
          "**/dist/**", "**/build/**", "**/.cache/**", "**/coverage/**",
        ]

        # Binary files are detected by content — a NUL byte in the first 8 KB,
        # which is git's own test — never by extension. These two catch what
        # survives that: checked-in datasets and generated parsers are large,
        # and minified bundles are made of enormous lines.
        #
        # The mean line, not the longest: one long line among many short ones is
        # a formatting choice, while a file made of long lines is generated.
        # Real source here averages 38 bytes a line, 68 at p99; a bundle runs to
        # thousands, so 400 sits in the gap rather than near either edge.
        max_file_bytes = 1000000
        max_mean_line_bytes = 400

        [impressions]
        mode = "extractive"           # extractive (default, zero-dep) | llm
        # llm mode refines extractive impressions via an OpenAI-compatible local endpoint
        endpoint = "http://localhost:1234/v1"
        model = "qwen3-4b-instruct"
        max_tokens_per_impression = 60
        batch = 8                      # throttled, idle-priority, never blocks indexing

        [taxonomy]
        # top-level roots of the memory tree; extendable without migration
        roots = ["/knowledge", "/user", "/sessions", "/machine", "/projects", "/people", "/concepts"]
        default_user = "jim"          # preference-kind facts route to /people/<default_user>/…
        # /knowledge holds the seeded corpus; /user holds what the user stated about
        # themselves, one entity per statement. Both were in use before they were listed
        # here — this list is documentation, not a constraint anything enforces yet.
        # Code is not a root: a codebase belongs to a project, so it lands at
        # /projects/<name>/code/<repo>/… beside that project's decisions (D27).

        [primer]
        max_tokens = 300
        """;
}
